using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization.Simulation;
using Xunit.Abstractions;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

/// <summary>Stress tester decorator that throws on a chosen run.</summary>
internal sealed class ThrowingTester(IStressTester inner, int throwOnRun) : IStressTester
{
    private int runs;

    public string AdapterName => inner.AdapterName;

    public Task CalibrateAsync(CancellationToken ct) => inner.CalibrateAsync(ct);

    public Task<StressResult> RunAsync(StressRequest request, IProgress<StressProgress>? progress, CancellationToken ct) =>
        ++runs == throwOnRun ? throw new InvalidOperationException("boom") : inner.RunAsync(request, progress, ct);

    public void Dispose() => inner.Dispose();
}

public class OptimizerLifecycleTests(ITestOutputHelper output)
{
    private const int Original = 50_000;
    private static readonly OptimizerSettings Quick = OptimizerSettings.For(OptimizationPreset.Quick);

    private static SimulatedGpuOptions NonZeroOriginal => new() { InitialOffsetKhz = Original };

    [Fact]
    public async Task OriginalOffsetsAreRestoredAfterSuccess()
    {
        var sim = Sim.Create(1, NonZeroOriginal);

        var result = await sim.Optimizer.RunAsync(Quick, null, CancellationToken.None);

        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.True(result.OffsetsRestored);
        Assert.All(sim.Offsets.OffsetsKhz, o => Assert.Equal(Original, o));
        Assert.All(result.OriginalOffsets.OffsetsKhz, o => Assert.Equal(Original, o));
    }

    [Fact]
    public async Task OriginalOffsetsAreRestoredAfterCancellation()
    {
        var sim = Sim.Create(1, NonZeroOriginal);
        using var cts = new CancellationTokenSource();
        var finished = 0;
        var events = new EventCollector
        {
            Hook = e =>
            {
                if (e is TestFinished && Interlocked.Increment(ref finished) == 6) cts.Cancel();
            },
        };

        var result = await sim.Optimizer.RunAsync(Quick, events, cts.Token);

        Assert.Equal(OptimizationStatus.Cancelled, result.Status);
        Assert.True(result.OffsetsRestored);
        Assert.All(sim.Offsets.OffsetsKhz, o => Assert.Equal(Original, o));
        Assert.Equal(6, result.Tests.Count);
        Assert.Null(((InMemorySessionStore)sim.Store).Load(result.SessionId)!.InFlight);
        Assert.Equal(SessionStatus.Cancelled, ((InMemorySessionStore)sim.Store).Load(result.SessionId)!.Status);
    }

    [Fact]
    public async Task OriginalOffsetsAreRestoredWhenAnExceptionEscapes()
    {
        var sim = Sim.Create(1, NonZeroOriginal, wrapTester: t => new ThrowingTester(t, 8));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sim.Optimizer.RunAsync(Quick, null, CancellationToken.None));

        Assert.Equal("boom", ex.Message);
        Assert.All(sim.Offsets.OffsetsKhz, o => Assert.Equal(Original, o));
        var saved = ((InMemorySessionStore)sim.Store).List().Single();
        Assert.Equal(SessionStatus.Failed, saved.Status);
        Assert.True(saved.OffsetsRestored);
        Assert.Null(saved.InFlight);
    }

    [Fact]
    public async Task CurrentReferenceIsMeasuredAtTheOriginalCurve()
    {
        var sim = Sim.Create(1, NonZeroOriginal);

        var result = await sim.Optimizer.RunAsync(Quick, null, CancellationToken.None);

        Assert.NotNull(result.StockReference);
        Assert.NotNull(result.CurrentReference);
        Assert.Equal(ReferenceKind.Current, result.CurrentReference.Kind);
        Assert.Contains(result.Tests, t => t.Kind == TestKind.ReferenceCurrent);
    }

    [Fact]
    public async Task UnstableOriginalCurve_IsWarnedAboutAndTheRunContinues()
    {
        var sim = Sim.Create(1, new SimulatedGpuOptions { InitialOffsetKhz = 400_000 });
        var events = new EventCollector();

        var result = await sim.Optimizer.RunAsync(Quick, events, CancellationToken.None);

        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.Null(result.CurrentReference);
        Assert.Contains(events.Of<Warning>(), w => w.Kind == WarningKind.CurrentCurveUnstable);
    }

    [Fact]
    public async Task ValidationFailure_RaisesTheMarginUntilTheCurvePasses()
    {
        var sim = Sim.Create(2, stressOptions: new SimulatedStressOptions { VariablePenaltyMhz = 90 });
        var events = new EventCollector();

        var result = await sim.Optimizer.RunAsync(Quick, events, CancellationToken.None);
        output.WriteLine(string.Join(", ", events.Of<Warning>().Select(w => $"{w.Kind}:{w.Detail}")));

        var recommended = result.Recommended!;
        Assert.True(recommended.Validated);
        Assert.True(recommended.ExtraMarginMhz is > 0 and <= 45);
        Assert.Contains(events.Of<Warning>(), w => w.Kind == WarningKind.MarginIncreased);
        Assert.Equal(recommended.ExtraMarginMhz / 15 + 1, result.Tests.Count(t => t.Kind == TestKind.Validation));
        Assert.Equal(
            CurveBuilder.BuildFinal(result.Stock, result.Anchors, recommended.CapAnchor, recommended.ExtraMarginMhz, result.OriginalOffsets).OffsetsKhz,
            recommended.Offsets.OffsetsKhz);
    }

    [Fact]
    public async Task ValidationThatNeverPasses_LeavesTheProposalUnvalidated()
    {
        var sim = Sim.Create(2, stressOptions: new SimulatedStressOptions { VariablePenaltyMhz = 250 });
        var events = new EventCollector();

        var result = await sim.Optimizer.RunAsync(Quick, events, CancellationToken.None);

        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.False(result.Recommended!.Validated);
        Assert.Equal(4, result.Tests.Count(t => t.Kind == TestKind.Validation));
        Assert.Contains(events.Of<Warning>(), w => w.Kind == WarningKind.ValidationFailed);
    }

    [Fact]
    public async Task ValidateAsync_ChecksAnyAnchorAsCapAndRestoresTheCurrentOffsets()
    {
        var sim = Sim.Create(2, NonZeroOriginal);
        var result = await sim.Optimizer.RunAsync(Quick, null, CancellationToken.None);
        var cap = result.Anchors[1];

        var proposal = await sim.Optimizer.ValidateAsync(result, cap, ProposalKind.Custom, null, CancellationToken.None);

        Assert.Equal(ProposalKind.Custom, proposal.Kind);
        Assert.Equal(cap, proposal.CapAnchor);
        Assert.True(proposal.Validated);
        Assert.All(sim.Offsets.OffsetsKhz, o => Assert.Equal(Original, o));
    }

    [Fact]
    public async Task AnOptimizerRunsOneOperationAtATime()
    {
        var sim = Sim.Create(2);
        var first = sim.Optimizer.RunAsync(Quick, null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sim.Optimizer.RunAsync(Quick, null, CancellationToken.None));
        await first;
    }
}
