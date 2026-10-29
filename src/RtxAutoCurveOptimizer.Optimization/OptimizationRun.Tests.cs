using System.Runtime.CompilerServices;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization;

internal sealed partial class OptimizationRun
{
    /// <summary>One test: curve to apply, load segments and whether telemetry must confirm the operating point.</summary>
    private sealed record TestSpec(
        TestKind Kind, int AnchorIndex, int PointIndex, int FrequencyKhz, int AnchorVoltageUv,
        CurveOffsets Offsets, IReadOnlyList<(LoadPattern Pattern, TimeSpan Duration)> Segments,
        double Intensity, bool CheckValidity);

    /// <summary>
    /// Runs one test and recovers afterwards: a driver reset reconnects the GPU; a test without telemetry is not
    /// judged, the GPU is reconnected and the test repeated once. A GPU that stays unreachable aborts the run.
    /// </summary>
    private async Task<TestRecord> ExecuteTestAsync(TestSpec spec, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var record = await ExecuteTestOnceAsync(spec, ct).ConfigureAwait(false);
            if (record.Outcome == StressOutcome.DeviceLost)
            {
                await RecoverFromDeviceLostAsync(ct).ConfigureAwait(false);
                return record;
            }

            if (!record.TelemetryLost) return record;
            Report(new Warning(WarningKind.TelemetryLost, record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await ReconnectGpuAsync(ct).ConfigureAwait(false);
            if (attempt >= MaxTelemetryLostRetries)
                throw new RunAbortException(OptimizationFailure.GpuUnresponsive, "GPU telemetry stayed unavailable after reconnecting.");
        }
    }

    /// <summary>
    /// Persists the in-flight marker, applies and verifies the curve, runs the segments with telemetry sampling and
    /// records the result. Averages, throughput and validity come from the first segment.
    /// </summary>
    private async Task<TestRecord> ExecuteTestOnceAsync(TestSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var startedAt = time.GetUtcNow();
        var total = spec.Segments.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration);
        var sequence = state.Tests.Count + 1;
        var inFlight = new InFlightTest(sequence, spec.Kind, spec.AnchorIndex, spec.PointIndex, spec.FrequencyKhz,
            total, spec.Segments[0].Pattern, spec.Intensity, startedAt);
        Update(s => s with { InFlight = inFlight });
        Report(new TestStarted(spec.AnchorIndex, spec.FrequencyKhz, total, spec.Kind));
        await ApplyVerifiedAsync(spec.Offsets, ct).ConfigureAwait(false);

        StressResult? first = null;
        List<Telemetry>? firstSamples = null;
        StressResult? failing = null;
        var elapsed = TimeSpan.Zero;
        long mismatches = 0;
        var readErrors = 0;
        foreach (var (pattern, duration) in spec.Segments)
        {
            var (result, samples, errors) = await RunSegmentAsync(pattern, duration, spec.Intensity, ct).ConfigureAwait(false);
            first ??= result;
            firstSamples ??= samples;
            readErrors += errors;
            elapsed += result.Elapsed;
            mismatches += result.MismatchCount;
            if (result.Outcome != StressOutcome.Passed)
            {
                failing = result;
                break;
            }
        }

        var outcome = failing?.Outcome ?? StressOutcome.Passed;
        if (outcome == StressOutcome.Cancelled)
        {
            if (ct.IsCancellationRequested)
            {
                Update(s => s with { InFlight = null });
                ct.ThrowIfCancellationRequested();
            }
            outcome = StressOutcome.Crashed;
        }

        var averages = TelemetryStats.Summarize(firstSamples!);
        var telemetryLost = outcome != StressOutcome.DeviceLost
                            && ((outcome == StressOutcome.Passed && firstSamples!.Count == 0) || readErrors > firstSamples!.Count);
        var valid = !telemetryLost && (!spec.CheckValidity
                    || TelemetryStats.IsValid(averages, spec.FrequencyKhz, spec.AnchorVoltageUv, Settings.ThermalDerateAllowanceMhz));
        var record = new TestRecord(
            sequence, spec.Kind, spec.AnchorIndex, spec.PointIndex, spec.FrequencyKhz,
            total, spec.Segments[0].Pattern, spec.Intensity,
            outcome, valid, elapsed, mismatches, first!.Throughput, averages, startedAt, failing?.Detail, telemetryLost);
        Update(s => s with { Tests = [.. s.Tests, record], InFlight = null });
        Report(new TestFinished(record));
        ReportEta();
        return record;
    }

    /// <summary>Applies offsets and reads them back; a write the driver did not take triggers one reconnect, then aborts.</summary>
    private async Task ApplyVerifiedAsync(CurveOffsets offsets, CancellationToken ct)
    {
        if (TryApply(offsets)) return;
        await ReconnectGpuAsync(ct).ConfigureAwait(false);
        if (!TryApply(offsets))
            throw new RunAbortException(OptimizationFailure.GpuUnresponsive, "The GPU did not take the test curve after reconnecting.");
    }

    private bool TryApply(CurveOffsets offsets)
    {
        try
        {
            gpu.ApplyOffsets(offsets);
            return Matches(gpu.ReadOffsets(), offsets);
        }
        catch (GpuAccessDeniedException)
        {
            throw;
        }
        catch (GpuControlException)
        {
            return false;
        }
    }

    /// <summary>True if every read-back offset is within 1 MHz of the requested (clamped) one.</summary>
    private static bool Matches(CurveOffsets actual, CurveOffsets expected) =>
        actual.OffsetsKhz.Count == expected.OffsetsKhz.Count
        && actual.OffsetsKhz.Zip(expected.OffsetsKhz).All(p => Math.Abs(p.First - Math.Clamp(p.Second, -CurveBuilder.MaxOffsetKhz, CurveBuilder.MaxOffsetKhz)) <= 1000);

    /// <summary>Starts the stress run, samples telemetry every 250 ms until it ends, and returns both plus failed reads.</summary>
    private async Task<(StressResult Result, List<Telemetry> Samples, int ReadErrors)> RunSegmentAsync(
        LoadPattern pattern, TimeSpan duration, double intensity, CancellationToken ct)
    {
        var samples = new List<Telemetry>();
        var errors = new StrongBox<int>();
        using var stopSampling = new CancellationTokenSource();
        var request = new StressRequest(duration, pattern, intensity);
        var runTask = tester.RunAsync(request, new InlineProgress<StressProgress>(p => Report(new StressProgressed(p))), ct);
        var samplerTask = SampleAsync(samples, errors, stopSampling.Token);
        try
        {
            return (await runTask.ConfigureAwait(false), samples, errors.Value);
        }
        finally
        {
            await stopSampling.CancelAsync().ConfigureAwait(false);
            await samplerTask.ConfigureAwait(false);
        }
    }

    private async Task SampleAsync(List<Telemetry> samples, StrongBox<int> errors, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TelemetryInterval, time, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var sample = gpu.ReadTelemetry();
                samples.Add(sample);
                Report(new TelemetrySampled(sample));
            }
            catch (GpuControlException)
            {
                errors.Value++;
            }
        }
    }

    /// <summary>Reconnects the GPU and recalibrates so goldens never come from an overclocked curve.</summary>
    private async Task RecoverFromDeviceLostAsync(CancellationToken ct)
    {
        Report(new Warning(WarningKind.DeviceLost, null));
        await ReconnectGpuAsync(ct).ConfigureAwait(false);
        await tester.CalibrateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the driver, reopens all handles, sets and verifies the stock curve and checks telemetry.
    /// Anything failing here means the GPU is not under control any more: the run aborts.
    /// </summary>
    private async Task ReconnectGpuAsync(CancellationToken ct)
    {
        await Task.Delay(RecoveryDelay, time, ct).ConfigureAwait(false);
        try
        {
            gpu.Reinitialize();
            gpu.ApplyOffsets(StockOffsets);
            if (!Matches(gpu.ReadOffsets(), StockOffsets))
                throw new GpuControlException("The stock curve did not apply after reconnecting.");
            gpu.ReadTelemetry();
        }
        catch (GpuAccessDeniedException)
        {
            throw;
        }
        catch (GpuControlException ex)
        {
            throw new RunAbortException(OptimizationFailure.GpuUnresponsive, ex.Message);
        }
    }

    private bool warmupDone;
    private bool referencesDone;
    private int anchorsAfterCurrent;
    private bool anchorActive;
    private int searchTestsInAnchor;
    private readonly List<int> completedSearchTests = [];

    /// <summary>Emits the remaining time: pending setup, current anchor, remaining anchors and validation.</summary>
    private void ReportEta()
    {
        var s = Settings;
        var avgTests = completedSearchTests.Count > 0 ? completedSearchTests.Average() : OptimizerSettings.DefaultTestsPerAnchor;
        var perAnchor = s.SearchTestDuration * avgTests + s.ConfirmTestDuration;
        var remaining = s.ValidationDuration + perAnchor * anchorsAfterCurrent;
        if (!warmupDone) remaining += s.WarmupDuration;
        if (!referencesDone) remaining += s.ReferenceDuration * 2;
        if (anchorActive)
            remaining += s.SearchTestDuration * Math.Max(0, avgTests - searchTestsInAnchor) + s.ConfirmTestDuration;
        Report(new EtaUpdated(remaining));
    }
}
