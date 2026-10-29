using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>
/// Orchestrates the optimization: references, anchor searches, proposals and validation.
/// One operation at a time; the original offsets are restored whenever a run ends.
/// </summary>
public sealed class CurveOptimizer
{
    private readonly IGpuControl gpu;
    private readonly IStressTester tester;
    private readonly ISessionStore store;
    private readonly TimeProvider time;
    private int busy;

    public CurveOptimizer(
        IGpuControl gpu, IStressTester tester, ISessionStore? sessionStore = null, TimeProvider? timeProvider = null)
    {
        this.gpu = gpu;
        this.tester = tester;
        store = sessionStore ?? NullSessionStore.Instance;
        time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs a complete optimization. Cancellation and expected failures are reported through
    /// <see cref="OptimizationResult.Status"/>; unexpected exceptions are rethrown after the offsets are restored.
    /// </summary>
    public async Task<OptimizationResult> RunAsync(
        OptimizerSettings settings, IProgress<OptimizerEvent>? progress, CancellationToken ct)
    {
        settings.Validate();
        EnterBusy();
        try
        {
            var state = OptimizationRun.CreateState(gpu, store, time, settings);
            var run = new OptimizationRun(gpu, tester, store, time, progress, state);
            return await run.ExecuteAsync(false, ct).ConfigureAwait(false);
        }
        finally
        {
            ExitBusy();
        }
    }

    /// <summary>
    /// Continues a recovered session (see <see cref="SessionRecovery"/>): completed anchors and references are
    /// kept, a crashed test counts as a failure at its frequency.
    /// </summary>
    public async Task<OptimizationResult> ResumeAsync(
        SessionState session, IProgress<OptimizerEvent>? progress, CancellationToken ct)
    {
        EnterBusy();
        try
        {
            if (!SessionRecovery.Matches(session, gpu))
                return session.ToResult() with
                {
                    Status = OptimizationStatus.Failed,
                    Failure = OptimizationFailure.SessionMismatch,
                    FailureDetail = "The session was recorded on a different GPU.",
                };

            var run = new OptimizationRun(gpu, tester, store, time, progress, session with { InFlight = null });
            return await run.ExecuteAsync(true, ct).ConfigureAwait(false);
        }
        finally
        {
            ExitBusy();
        }
    }

    /// <summary>
    /// Validates the final curve capped at any anchor of <paramref name="result"/> (Variable load, margin escalation).
    /// Restores the current offsets afterwards; throws <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public async Task<ProfileProposal> ValidateAsync(
        OptimizationResult result, AnchorResult capAnchor, ProposalKind kind,
        IProgress<OptimizerEvent>? progress, CancellationToken ct, int startExtraMarginMhz = 0)
    {
        EnterBusy();
        try
        {
            var now = time.GetUtcNow();
            var state = new SessionState(
                result.SessionId, SessionStatus.Completed, now, now, gpu.Identity, result.Stock, result.OriginalOffsets,
                result.Settings, result.StockReference, result.CurrentReference, result.Tests, result.Anchors,
                result.Proposals, null, true, OptimizationFailure.None, null);
            var run = new OptimizationRun(gpu, tester, NullSessionStore.Instance, time, progress, state);
            return await run.ValidateStandaloneAsync(kind, capAnchor, startExtraMarginMhz, ct).ConfigureAwait(false);
        }
        finally
        {
            ExitBusy();
        }
    }

    private void EnterBusy()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new InvalidOperationException("The optimizer is already running an operation.");
    }

    private void ExitBusy() => Interlocked.Exchange(ref busy, 0);
}
