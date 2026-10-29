using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization;

internal sealed partial class OptimizationRun
{
    private sealed record AnchorSlot(int Index, int PointIndex);

    /// <summary>Mutable search knowledge of one anchor; rebuilt from the test records when a session is resumed.</summary>
    private sealed class AnchorSearch
    {
        public Dictionary<int, Verdict> Known { get; } = [];
        public int ConfirmFailures { get; set; }
        public double Intensity { get; set; } = 1.0;
        public int SearchTests { get; set; }
    }

    private List<AnchorSlot> ResolveAnchors() =>
        [.. Settings.AnchorVoltagesMv
            .Select(mv => CurveBuilder.NearestPointIndex(Stock, mv))
            .Distinct().Order()
            .Select((point, index) => new AnchorSlot(index, point))];

    private void InitEta()
    {
        var done = state.Anchors.Select(a => a.AnchorIndex).ToHashSet();
        anchorsAfterCurrent = ResolveAnchors().Count(a => !done.Contains(a.Index));
        completedSearchTests.AddRange(state.Anchors.Select(a =>
            state.Tests.Count(t => t.AnchorIndex == a.AnchorIndex && t.Kind == TestKind.Search)));
    }

    private async Task SearchAnchorsAsync(CancellationToken ct)
    {
        Report(new PhaseChanged(OptimizerPhase.SearchingAnchors));
        foreach (var slot in ResolveAnchors())
        {
            if (state.Anchors.Any(a => a.AnchorIndex == slot.Index)) continue;

            anchorsAfterCurrent--;
            anchorActive = true;
            var result = await SearchAnchorAsync(slot, ct).ConfigureAwait(false);
            anchorActive = false;
            completedSearchTests.Add(searchTestsInAnchor);
            if (result is null) continue;

            Update(s => s with { Anchors = [.. s.Anchors, result] });
            Report(new AnchorCompleted(result));
            if (result.Limited) Report(new Warning(WarningKind.AnchorLimited, slot.Index.ToString()));
        }
    }

    /// <summary>Gallop/bisect search followed by confirmation; null if the anchor yields no usable result.</summary>
    private async Task<AnchorResult?> SearchAnchorAsync(AnchorSlot slot, CancellationToken ct)
    {
        var point = Stock.Points[slot.PointIndex];
        var search = RebuildSearch(slot, point);
        searchTestsInAnchor = search.SearchTests;
        var startDelta = StartDelta(slot, point);
        var maxDelta = MaxDeltaMhz(point);

        while (true)
        {
            var step = SearchPlanner.Next(search.Known, startDelta, Settings.StepMhz, Settings.GallopStepMhz, maxDelta);
            // After a driver reset, no more probing above a known pass: settle on the highest pass instead.
            var best = search.Known.Where(k => k.Value == Verdict.Pass).Select(k => (int?)k.Key).Max();
            if (step.Action == SearchAction.Test && ResetsAt(slot) >= MaxResetsPerAnchor && best is not null)
                step = new SearchStep(SearchAction.Done, best.Value);
            if (step.Action == SearchAction.Abort)
            {
                if (search.Known.TryGetValue(step.DeltaMhz, out var v) && v == Verdict.Invalid)
                {
                    Report(new Warning(WarningKind.AnchorSkipped, slot.Index.ToString()));
                    return null;
                }
                throw new RunAbortException(OptimizationFailure.StockUnstable,
                    $"Anchor {slot.Index} is unstable at the stock frequency.");
            }

            if (step.Action == SearchAction.Done)
            {
                var confirmed = await ConfirmAsync(slot, point, step.DeltaMhz, search, ct).ConfigureAwait(false);
                return confirmed is null ? null : await ColdConfirmAsync(slot, point, confirmed, ct).ConfigureAwait(false);
            }

            if (!FlatTestFits(slot, point, step.DeltaMhz))
            {
                Report(new Warning(WarningKind.AnchorSkipped, slot.Index.ToString()));
                return null;
            }

            var record = await RunAnchorTestAsync(slot, point, TestKind.Search, step.DeltaMhz, search, ct).ConfigureAwait(false);
            if (VoltageAboveAnchor(record, point))
            {
                Report(new Warning(WarningKind.AnchorSkipped, slot.Index.ToString()));
                return null;
            }

            search.Known[step.DeltaMhz] = VerdictOf(record);
            search.SearchTests++;
            searchTestsInAnchor = search.SearchTests;
        }
    }

    /// <summary>True if the flat test curve at the anchor's stock frequency plus <paramref name="deltaMhz"/> stays within the driver's offset range.</summary>
    private bool FlatTestFits(AnchorSlot slot, CurvePoint point, int deltaMhz) =>
        CurveBuilder.BuildFlatTest(Stock, slot.PointIndex, point.StockKhz + deltaMhz * 1000)
            .OffsetsKhz.All(o => Math.Abs(o) <= CurveBuilder.MaxOffsetKhz);

    /// <summary>Confirms the bracketed frequency; steps down by one grid step per failure (max 3 attempts).</summary>
    private async Task<AnchorResult?> ConfirmAsync(
        AnchorSlot slot, CurvePoint point, int passDelta, AnchorSearch search, CancellationToken ct)
    {
        for (var attempt = search.ConfirmFailures; attempt < MaxConfirmAttempts; attempt++)
        {
            // One step below the highest steady pass: the pulsed part tipped exactly there into driver resets.
            var delta = Math.Max(0, passDelta - (attempt + 1) * Settings.StepMhz);
            if (delta < 0 || !FlatTestFits(slot, point, delta) || ResetsAt(slot) > MaxResetsPerAnchor) break;

            var record = await RunAnchorTestAsync(slot, point, TestKind.Confirm, delta, search, ct).ConfigureAwait(false);
            if (VoltageAboveAnchor(record, point))
            {
                Report(new Warning(WarningKind.AnchorSkipped, slot.Index.ToString()));
                return null;
            }

            if (record.Passed) return BuildAnchorResult(slot, point, record, search);
            search.ConfirmFailures++;
        }

        Report(new Warning(WarningKind.AnchorUnconfirmed, slot.Index.ToString()));
        return null;
    }

    /// <summary>
    /// Cold confirm: the hot tests only prove the clock the GPU ran after thermal derating. After a cool-down, short
    /// bursts run the flat test curve while the GPU is still cold, so it runs the full curve value. Tries the middle
    /// between the hot measured clock and the confirmed curve value, then the full value (skipped once any cold burst
    /// failed). An error-free burst on the anchor voltage proves the highest clock it really ran.
    /// </summary>
    private async Task<AnchorResult> ColdConfirmAsync(AnchorSlot slot, CurvePoint point, AnchorResult result, CancellationToken ct)
    {
        if (!Settings.ColdConfirm) return result;
        var stepKhz = Settings.StepMhz * 1000;
        var hotKhz = ProposalBuilder.ProvenKhz(result);
        if (result.MaxStableKhz - hotKhz < 2 * stepKhz) return result;

        var middle = hotKhz + (result.MaxStableKhz - hotKhz) / 2 / stepKhz * stepKhz;
        var fullFailedBefore = state.Tests.Any(t => t.Kind == TestKind.ColdConfirm && t.Outcome != StressOutcome.Passed);
        int[] targets = fullFailedBefore ? [middle] : [middle, result.MaxStableKhz];

        var proven = 0;
        foreach (var target in targets)
        {
            await CoolDownAsync(slot, ct).ConfigureAwait(false);
            var spec = new TestSpec(TestKind.ColdConfirm, slot.Index, slot.PointIndex, target, point.VoltageUv,
                CurveBuilder.BuildFlatTest(Stock, slot.PointIndex, target), [(LoadPattern.Steady, ColdBurstDuration)],
                result.Intensity, false);
            var record = await ExecuteTestAsync(spec, ct).ConfigureAwait(false);
            if (!record.Passed || record.Telemetry is not { } t
                || Math.Abs(t.MedianVoltageUv - point.VoltageUv) > TelemetryStats.VoltageToleranceUv) break;
            proven = Math.Max(proven, Math.Min(target, (int)Math.Round(t.MaxClockMhz) * 1000));
        }

        var cold = result with { ColdProvenKhz = proven };
        return cold with { RecommendedKhz = ProposalBuilder.RecommendedKhz(cold, Settings.SafetyMarginMhz) };
    }

    /// <summary>
    /// Idles until the GPU is at most <see cref="ColdTemperatureC"/>, its temperature stopped falling for
    /// <see cref="CoolDownSettled"/>, or <see cref="CoolDownMax"/> passed; reports telemetry meanwhile.
    /// </summary>
    private async Task CoolDownAsync(AnchorSlot slot, CancellationToken ct)
    {
        Report(new Warning(WarningKind.CoolingDown, slot.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var now = time.GetUtcNow();
        var deadline = now + CoolDownMax;
        var lowest = int.MaxValue;
        var lowestAt = now;
        while ((now = time.GetUtcNow()) < deadline && now - lowestAt < CoolDownSettled)
        {
            await Task.Delay(CoolDownPoll, time, ct).ConfigureAwait(false);
            try
            {
                var sample = gpu.ReadTelemetry();
                Report(new TelemetrySampled(sample));
                if (sample.TemperatureC <= ColdTemperatureC) return;
                if (sample.TemperatureC < lowest)
                {
                    lowest = sample.TemperatureC;
                    lowestAt = time.GetUtcNow();
                }
            }
            catch (GpuControlException)
            {
            }
        }
    }

    /// <summary>True if a passed test ran above the anchor voltage, i.e. the test curve was not flat as intended.</summary>
    private static bool VoltageAboveAnchor(TestRecord record, CurvePoint point) =>
        record.Outcome == StressOutcome.Passed && record.Telemetry is { } t && TelemetryStats.VoltageAboveAnchor(t, point.VoltageUv);

    /// <summary>
    /// One test at the anchor; if the GPU ran below the anchor voltage (a lower point under a real cap), repeats with
    /// lower intensity (1.0, 0.8, 0.6). The reduced intensity sticks for the anchor.
    /// </summary>
    private async Task<TestRecord> RunAnchorTestAsync(
        AnchorSlot slot, CurvePoint point, TestKind kind, int deltaMhz, AnchorSearch search, CancellationToken ct)
    {
        var frequency = point.StockKhz + deltaMhz * 1000;
        var offsets = CurveBuilder.BuildFlatTest(Stock, slot.PointIndex, frequency);
        var duration = kind == TestKind.Confirm ? Settings.ConfirmTestDuration : Settings.SearchTestDuration;
        IReadOnlyList<(LoadPattern, TimeSpan)> segments = kind == TestKind.Confirm
            ? [(LoadPattern.Steady, duration * 2 / 3), (LoadPattern.Pulsed, duration - duration * 2 / 3)]
            : [(LoadPattern.Steady, duration)];

        TestRecord? record = null;
        foreach (var intensity in IntensityChain.Where(i => i <= search.Intensity + 1e-9))
        {
            if (record is not null)
                Report(new Warning(WarningKind.IntensityReduced, intensity.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));

            search.Intensity = intensity;
            var spec = new TestSpec(kind, slot.Index, slot.PointIndex, frequency, point.VoltageUv, offsets, segments, intensity, true);
            record = await ExecuteTestAsync(spec, ct).ConfigureAwait(false);
            if (record.Outcome != StressOutcome.Passed || record.Valid) break;
            if (record.Telemetry is null || !TelemetryStats.VoltageBelowAnchor(record.Telemetry, point.VoltageUv)) break;
        }

        return record!;
    }

    private AnchorResult BuildAnchorResult(AnchorSlot slot, CurvePoint point, TestRecord confirm, AnchorSearch search)
    {
        var averages = confirm.Telemetry!;
        var normalized = confirm.Throughput / confirm.Intensity;
        var relative = state.StockReference!.Throughput > 0 ? normalized / state.StockReference.Throughput : 0;
        var perWatt = averages.AvgPowerW > 0 ? confirm.Throughput / averages.AvgPowerW : 0;
        var limited = confirm.Intensity < 1.0 || search.Known.Values.Any(v => v == Verdict.Invalid);
        return new AnchorResult(
            slot.Index, slot.PointIndex, point.VoltageUv, point.StockKhz,
            confirm.FrequencyKhz, ProposalBuilder.RecommendedKhz(confirm.FrequencyKhz, averages.AvgClockMhz, Settings.SafetyMarginMhz),
            averages.AvgPowerW, averages.AvgClockMhz, averages.AvgVoltageUv, averages.AvgTemperatureC,
            confirm.Throughput, relative, perWatt, limited, confirm.Intensity);
    }

    /// <summary>
    /// First delta to test: the highest frequency already stable at a lower voltage (safe with more voltage).
    /// Without one: one step below the user's original offset if that curve passed its reference, else +150 MHz.
    /// Never above <see cref="MaxDeltaMhz"/>.
    /// </summary>
    private int StartDelta(AnchorSlot slot, CurvePoint point)
    {
        var provenKhz = state.Anchors.Where(a => a.VoltageUv < point.VoltageUv).Select(a => a.MaxStableKhz).DefaultIfEmpty(0).Max();
        int start;
        if (provenKhz > 0)
        {
            start = SearchPlanner.RoundUpToGrid((provenKhz - point.StockKhz) / 1000.0, Settings.StepMhz);
        }
        else
        {
            var originalMhz = state.CurrentReference is null ? 0 : state.OriginalOffsets.OffsetsKhz[slot.PointIndex] / 1000;
            var belowOriginal = originalMhz / Settings.StepMhz * Settings.StepMhz - Settings.StepMhz;
            start = Math.Max(FirstAnchorDeltaMhz, belowOriginal);
        }
        return Math.Clamp(start, 0, MaxDeltaMhz(point));
    }

    /// <summary>
    /// Highest delta the search may test: below the lowest delta that already failed at a lower voltage
    /// (the stable delta shrinks with voltage), and within MaxOffsetMhz.
    /// </summary>
    private int MaxDeltaMhz(CurvePoint point)
    {
        // Lowest failing delta per lower-voltage point, i.e. the limits that were really crossed. A Crashed test
        // only means the process died (possibly the UI), so it bounds its own anchor but not higher ones.
        var limits = state.Tests
            .Where(t => t.PointIndex >= 0 && t.Kind is TestKind.Search or TestKind.Confirm
                        && !t.TelemetryLost && t.Outcome is StressOutcome.DeviceLost or StressOutcome.ComputeError
                        && Stock.Points[t.PointIndex].VoltageUv < point.VoltageUv)
            .GroupBy(t => t.PointIndex)
            .Select(g => (VoltageUv: Stock.Points[g.Key].VoltageUv, Delta: g.Min(t => (t.FrequencyKhz - Stock.Points[g.Key].StockKhz) / 1000.0)))
            .OrderBy(l => l.VoltageUv)
            .ToList();
        if (limits.Count == 0) return Settings.MaxOffsetMhz;

        // After two crossed limits the next one is extrapolated (never rising), so it does not need a driver reset.
        var bound = limits.Min(l => l.Delta);
        if (limits.Count >= 2)
        {
            var (a, b) = (limits[^2], limits[^1]);
            var slope = Math.Min(0, (b.Delta - a.Delta) / (b.VoltageUv - a.VoltageUv));
            bound = Math.Min(bound, b.Delta + slope * (point.VoltageUv - b.VoltageUv));
        }
        return Math.Clamp((int)Math.Floor(bound) - Settings.StepMhz, 0, Settings.MaxOffsetMhz);
    }

    /// <summary>
    /// Driver resets recorded for the anchor so far. A Crashed test does not count: it is often the next test
    /// running into a driver that has not recovered yet, not an unstable clock.
    /// </summary>
    private int ResetsAt(AnchorSlot slot) =>
        state.Tests.Count(t => t.AnchorIndex == slot.Index && t.Outcome == StressOutcome.DeviceLost);

    private AnchorSearch RebuildSearch(AnchorSlot slot, CurvePoint point)
    {
        var search = new AnchorSearch();
        foreach (var t in state.Tests.Where(t => t.AnchorIndex == slot.Index && !t.TelemetryLost
                                                 && t.Kind is TestKind.Search or TestKind.Confirm))
        {
            search.Intensity = Math.Min(search.Intensity, t.Intensity);
            if (t.Kind == TestKind.Confirm)
            {
                if (!t.Passed) search.ConfirmFailures++;
                continue;
            }

            search.SearchTests++;
            var delta = (t.FrequencyKhz - point.StockKhz) / 1000;
            var verdict = VerdictOf(t);
            if (!search.Known.TryGetValue(delta, out var existing) || Rank(verdict) > Rank(existing))
                search.Known[delta] = verdict;
        }
        return search;
    }

    private static int Rank(Verdict verdict) => verdict switch { Verdict.Pass => 0, Verdict.Invalid => 1, _ => 2 };

    private static Verdict VerdictOf(TestRecord record) =>
        record.Outcome != StressOutcome.Passed ? Verdict.Fail
        : record.Valid ? Verdict.Pass
        : Verdict.Invalid;
}
