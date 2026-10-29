using System.Runtime.ExceptionServices;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>Raised inside a run for expected, reportable failures.</summary>
internal sealed class RunAbortException(OptimizationFailure failure, string message) : Exception(message)
{
    public OptimizationFailure Failure { get; } = failure;
}

/// <summary>Progress sink that invokes the callback on the reporting thread.</summary>
internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

/// <summary>One optimization run: owns the session state and drives phases, searches and tests.</summary>
internal sealed partial class OptimizationRun
{
    private static readonly double[] IntensityChain = [1.0, 0.8, 0.6];
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(10);

    private const int MaxStockAttempts = 3;
    private const int MaxConfirmAttempts = 3;
    private const int MaxValidationRetries = 3;
    private const int ValidationMarginStepMhz = 15;
    private const int FirstAnchorDeltaMhz = 150;
    private const int MaxResetsPerAnchor = 1;
    private const int MaxTelemetryLostRetries = 1;
    private static readonly TimeSpan ColdBurstDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CoolDownMax = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CoolDownPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CoolDownSettled = TimeSpan.FromSeconds(15);
    private const int ColdTemperatureC = 40;

    private readonly IGpuControl gpu;
    private readonly IStressTester tester;
    private readonly ISessionStore store;
    private readonly TimeProvider time;
    private readonly IProgress<OptimizerEvent>? progress;
    private SessionState state;

    public OptimizationRun(
        IGpuControl gpu, IStressTester tester, ISessionStore store, TimeProvider time,
        IProgress<OptimizerEvent>? progress, SessionState state)
    {
        this.gpu = gpu;
        this.tester = tester;
        this.store = store;
        this.time = time;
        this.progress = progress;
        this.state = state;
    }

    private OptimizerSettings Settings => state.Settings;

    private StockCurve Stock => state.Stock;

    private CurveOffsets StockOffsets => CurveOffsets.Zero(Stock.Points.Count);

    /// <summary>Creates the initial state of a fresh run from the current GPU.</summary>
    public static SessionState CreateState(IGpuControl gpu, ISessionStore store, TimeProvider time, OptimizerSettings settings)
    {
        var stock = gpu.ReadStockCurve();
        var original = gpu.ReadOffsets();
        if (original.OffsetsKhz.Count != stock.Points.Count)
            throw new InvalidOperationException("The GPU reported a different number of offsets than curve points.");

        var now = time.GetUtcNow();
        var baseId = time.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var id = baseId;
        for (var n = 2; store.Load(id) is not null; n++) id = $"{baseId}-{n}";

        return new SessionState(
            id, SessionStatus.InProgress, now, now, gpu.Identity, stock, original, settings,
            null, null, [], [], [], null, false, OptimizationFailure.None, null);
    }

    /// <summary>Runs all phases, always restores the original offsets and returns the outcome; unexpected exceptions are rethrown after restoring.</summary>
    public async Task<OptimizationResult> ExecuteAsync(bool resumed, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var status = SessionStatus.Completed;
        var failure = OptimizationFailure.None;
        string? detail = null;
        Exception? unexpected = null;
        var touchedGpu = false;

        Update(s => s with { Status = SessionStatus.InProgress, OffsetsRestored = false, InFlight = null });
        InitEta();
        Report(new PhaseChanged(OptimizerPhase.Preparing));

        try
        {
            if (!gpu.CanWrite)
                throw new RunAbortException(OptimizationFailure.GpuAccessDenied, "The process is not elevated.");
            if (resumed) Report(new Warning(WarningKind.ResumedFromSession, state.Id));

            touchedGpu = true;
            await PrepareAsync(ct).ConfigureAwait(false);
            await MeasureReferencesAsync(ct).ConfigureAwait(false);
            await SearchAnchorsAsync(ct).ConfigureAwait(false);
            BuildProposals();
            await ValidateRecommendedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = SessionStatus.Cancelled;
        }
        catch (RunAbortException ex)
        {
            // An unresponsive GPU usually needs a reboot; keep the session resumable for the next start.
            var next = ex.Failure == OptimizationFailure.GpuUnresponsive ? SessionStatus.InProgress : SessionStatus.Failed;
            (status, failure, detail) = (next, ex.Failure, ex.Message);
        }
        catch (GpuAccessDeniedException ex)
        {
            (status, failure, detail) = (SessionStatus.Failed, OptimizationFailure.GpuAccessDenied, ex.Message);
        }
        catch (GpuControlException ex)
        {
            (status, failure, detail) = (SessionStatus.Failed, OptimizationFailure.GpuControlError, ex.Message);
        }
        catch (Exception ex)
        {
            unexpected = ex;
            (status, failure, detail) = (SessionStatus.Failed, OptimizationFailure.None, ex.Message);
        }

        Report(new PhaseChanged(OptimizerPhase.Restoring));
        var restored = !touchedGpu || RestoreOriginals();
        Update(s => s with
        {
            Status = status, Failure = failure, FailureDetail = detail, InFlight = null, OffsetsRestored = restored,
        });
        Report(new PhaseChanged(OptimizerPhase.Finished));

        if (unexpected is not null) ExceptionDispatchInfo.Capture(unexpected).Throw();
        return state.ToResult() with { Elapsed = time.GetElapsedTime(started) };
    }

    /// <summary>Validates one cap anchor outside a full run: applies stock, calibrates, validates, restores the current offsets.</summary>
    public async Task<ProfileProposal> ValidateStandaloneAsync(
        ProposalKind kind, AnchorResult cap, int startMarginMhz, CancellationToken ct)
    {
        var current = gpu.ReadOffsets();
        try
        {
            Report(new PhaseChanged(OptimizerPhase.Calibrating));
            gpu.ApplyOffsets(StockOffsets);
            await tester.CalibrateAsync(ct).ConfigureAwait(false);
            Report(new PhaseChanged(OptimizerPhase.Validating));
            return await ValidateProposalAsync(kind, cap, startMarginMhz, ct).ConfigureAwait(false);
        }
        finally
        {
            Report(new PhaseChanged(OptimizerPhase.Restoring));
            try
            {
                gpu.ApplyOffsets(current);
            }
            catch (GpuControlException ex)
            {
                Report(new Warning(WarningKind.RestoreFailed, ex.Message));
            }
            Report(new PhaseChanged(OptimizerPhase.Finished));
        }
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        Report(new PhaseChanged(OptimizerPhase.Calibrating));
        gpu.ApplyOffsets(StockOffsets);
        await tester.CalibrateAsync(ct).ConfigureAwait(false);

        Report(new PhaseChanged(OptimizerPhase.WarmingUp));
        await RunStockTestAsync(TestKind.Warmup, StockOffsets, Settings.WarmupDuration, ct).ConfigureAwait(false);
        warmupDone = true;
    }

    private async Task MeasureReferencesAsync(CancellationToken ct)
    {
        Report(new PhaseChanged(OptimizerPhase.MeasuringReferences));

        if (state.StockReference is null)
        {
            var rec = await RunStockTestAsync(TestKind.ReferenceStock, StockOffsets, Settings.ReferenceDuration, ct)
                .ConfigureAwait(false);
            var reference = new ReferenceMeasurement(ReferenceKind.Stock, rec.Telemetry!, rec.Throughput);
            Update(s => s with { StockReference = reference });
        }

        referencesDone = true;
        if (state.CurrentReference is not null || state.Tests.Any(t => t.Kind == TestKind.ReferenceCurrent)) return;

        var original = state.OriginalOffsets;
        if (original.OffsetsKhz.All(o => o == 0))
        {
            var copy = state.StockReference! with { Kind = ReferenceKind.Current };
            Update(s => s with { CurrentReference = copy });
            return;
        }

        var spec = new TestSpec(TestKind.ReferenceCurrent, -1, -1, 0, 0, original,
            [(LoadPattern.Steady, Settings.ReferenceDuration)], 1.0, false);
        var current = await ExecuteTestAsync(spec, ct).ConfigureAwait(false);
        if (current.Passed)
            Update(s => s with { CurrentReference = new ReferenceMeasurement(ReferenceKind.Current, current.Telemetry!, current.Throughput) });
        else
            Report(new Warning(WarningKind.CurrentCurveUnstable, null));
    }

    /// <summary>Steady test at the stock curve; retries after device loss, aborts the run if it keeps failing.</summary>
    private async Task<TestRecord> RunStockTestAsync(TestKind kind, CurveOffsets offsets, TimeSpan duration, CancellationToken ct)
    {
        TestRecord? last = null;
        for (var attempt = 0; attempt < MaxStockAttempts; attempt++)
        {
            var spec = new TestSpec(kind, -1, -1, 0, 0, offsets, [(LoadPattern.Steady, duration)], 1.0, false);
            last = await ExecuteTestAsync(spec, ct).ConfigureAwait(false);
            if (last.Passed) return last;
            if (last.Outcome != StressOutcome.DeviceLost) break;
        }

        throw new RunAbortException(OptimizationFailure.StockUnstable,
            $"{kind} test failed at the stock curve ({last?.Outcome}).");
    }

    private void BuildProposals()
    {
        Report(new PhaseChanged(OptimizerPhase.BuildingProposals));
        if (state.Anchors.Count == 0)
            throw new RunAbortException(OptimizationFailure.NoUsableAnchors, "No anchor produced a usable result.");
        var proposals = ProposalBuilder.Build(Stock, state.Anchors, state.OriginalOffsets);
        Update(s => s with { Proposals = proposals });
    }

    private async Task ValidateRecommendedAsync(CancellationToken ct)
    {
        Report(new PhaseChanged(OptimizerPhase.Validating));
        var recommended = state.Proposals.First(p => p.Kind == ProposalKind.Efficient);
        var validated = await ValidateProposalAsync(recommended.Kind, recommended.CapAnchor, 0, ct).ConfigureAwait(false);
        Update(s => s with { Proposals = [.. s.Proposals.Select(p => p.Kind == validated.Kind ? validated : p)] });
    }

    /// <summary>Variable-load validation of the final curve; raises the extra margin by 15 MHz per failure (max 3 retries).</summary>
    private async Task<ProfileProposal> ValidateProposalAsync(
        ProposalKind kind, AnchorResult cap, int startMarginMhz, CancellationToken ct)
    {
        ProfileProposal proposal;
        var attempt = 0;
        while (true)
        {
            var margin = startMarginMhz + attempt * ValidationMarginStepMhz;
            proposal = ProposalBuilder.Create(Stock, state.Anchors, kind, cap, margin, false, state.OriginalOffsets);
            var spec = new TestSpec(TestKind.Validation, cap.AnchorIndex, cap.PointIndex,
                cap.RecommendedKhz - margin * 1000, cap.VoltageUv, proposal.Offsets,
                [(LoadPattern.Variable, Settings.ValidationDuration)], 1.0, false);
            var rec = await ExecuteTestAsync(spec, ct).ConfigureAwait(false);
            if (rec.Passed) return proposal with { Validated = true };

            if (attempt >= MaxValidationRetries) break;
            attempt++;
            Report(new Warning(WarningKind.MarginIncreased, (startMarginMhz + attempt * ValidationMarginStepMhz).ToString()));
        }

        Report(new Warning(WarningKind.ValidationFailed, null));
        return proposal with { Validated = false };
    }

    private bool RestoreOriginals()
    {
        try
        {
            gpu.ApplyOffsets(state.OriginalOffsets);
            return true;
        }
        catch (Exception ex)
        {
            Report(new Warning(WarningKind.RestoreFailed, ex.Message));
            return false;
        }
    }

    private void Update(Func<SessionState, SessionState> change)
    {
        state = change(state) with { UpdatedAt = time.GetUtcNow() };
        store.Save(state);
    }

    private void Report(OptimizerEvent e) => progress?.Report(e);
}
