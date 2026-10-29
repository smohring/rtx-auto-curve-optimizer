using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization.Simulation;
using Xunit.Abstractions;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

/// <summary>
/// Reproduces the failure modes of the first Standard run on the RTX 5080: dead telemetry after a driver reset,
/// curve writes that silently did nothing, and a first test far above the stable limit that hung the machine.
/// </summary>
public sealed class SafetyTests(ITestOutputHelper output)
{
    private static readonly OptimizerSettings Quick = OptimizerSettings.For(OptimizationPreset.Quick);
    private static readonly OptimizerSettings Standard = OptimizerSettings.For(OptimizationPreset.Standard);

    /// <summary>Every failure is a driver reset after which the handles are stale until reinitialized.</summary>
    private static readonly SimulatedGpuOptions StaleAfterReset = new() { StaleHandlesAfterDeviceLost = true };

    private static readonly SimulatedStressOptions AlwaysDeviceLost = new() { DeviceLostFraction = 1.0 };

    /// <summary>Stable delta like the measured card: above 600 MHz up to 920 mV, then falling to 380 MHz at 1050 mV.</summary>
    private static double MeasuredLikeDelta(int mv) => mv <= 920 ? 650 - (mv - 850) * 0.6 : 608 - (mv - 920) * (228.0 / 130);

    private async Task<(Sim Sim, OptimizationResult Result, EventCollector Events)> RunAsync(
        OptimizerSettings settings, SimulatedGpuOptions gpuOptions, SimulatedStressOptions stressOptions, int seed = 1)
    {
        var sim = Sim.Create(seed, gpuOptions, stressOptions);
        var events = new EventCollector();
        var result = await sim.Optimizer.RunAsync(settings, events, CancellationToken.None);
        output.WriteLine(TestFormat.Summary(result));
        output.WriteLine(TestFormat.AnchorTable(result, sim.Gpu));
        foreach (var t in result.Tests)
            output.WriteLine($"#{t.Sequence} {t.Kind} a{t.AnchorIndex} {t.FrequencyKhz / 1000} MHz {t.Outcome} valid={t.Valid} samples={t.Telemetry?.SampleCount}");
        output.WriteLine($"reinitialize={sim.Gpu.ReinitializeCount} maxOvershoot={sim.Tester.MaxOvershootMhz:F0} MHz");
        return (sim, result, events);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DriverResetWithStaleHandles_ReinitializesAndStillFindsEveryAnchor(int seed)
    {
        var (sim, result, _) = await RunAsync(Quick, StaleAfterReset, AlwaysDeviceLost, seed);

        var resets = result.Tests.Count(t => t.Outcome == StressOutcome.DeviceLost);
        Assert.True(resets > 0, "the scenario must contain driver resets");
        Assert.True(sim.Gpu.ReinitializeCount >= resets, $"{sim.Gpu.ReinitializeCount} reinitializations for {resets} resets");
        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.Equal([0, 1, 2, 3], result.Anchors.Select(a => a.AnchorIndex));
        foreach (var a in result.Anchors)
            Assert.InRange(sim.Gpu.TrueLimitKhz(a.PointIndex) - a.MaxStableKhz, 0, 60_000);
    }

    [Fact]
    public async Task DriverResetWithStaleHandles_NoTestIsJudgedWithoutTelemetry()
    {
        var (_, result, _) = await RunAsync(Quick, StaleAfterReset, AlwaysDeviceLost);

        var blind = result.Tests.Where(t => t.Kind is TestKind.Search or TestKind.Confirm
                                            && t.Outcome == StressOutcome.Passed
                                            && (t.Telemetry is null || t.Telemetry.SampleCount == 0)
                                            && !t.TelemetryLost);
        Assert.Empty(blind);
    }

    [Fact]
    public async Task GpuThatDoesNotComeBack_StopsTheRunInsteadOfTestingBlind()
    {
        var gpu = new SimulatedGpuOptions { StaleHandlesAfterDeviceLost = true, ReinitializeFails = true };
        var (_, result, _) = await RunAsync(Quick, gpu, AlwaysDeviceLost);

        Assert.Equal(OptimizationStatus.Failed, result.Status);
        Assert.Equal(OptimizationFailure.GpuUnresponsive, result.Failure);
        var firstReset = result.Tests.First(t => t.Outcome == StressOutcome.DeviceLost).Sequence;
        Assert.DoesNotContain(result.Tests, t => t.Sequence > firstReset && t.Kind is TestKind.Search or TestKind.Confirm);
    }

    [Fact]
    public async Task GpuThatDoesNotComeBack_LeavesTheSessionResumableAfterARestart()
    {
        var gpu = new SimulatedGpuOptions { StaleHandlesAfterDeviceLost = true, ReinitializeFails = true };
        var (sim, result, _) = await RunAsync(Quick, gpu, AlwaysDeviceLost);

        var unfinished = new SessionRecovery(sim.Store).FindUnfinished();
        Assert.NotNull(unfinished);
        Assert.Equal(result.SessionId, unfinished.Id);
        Assert.Equal(OptimizationFailure.GpuUnresponsive, unfinished.Failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task FallingLimitAndDriverResets_NoTestOvershootsTheLimitByMoreThanOneGallopStep(int seed)
    {
        var gpu = new SimulatedGpuOptions
        {
            TrueDeltaMhz = MeasuredLikeDelta, PowerCapW = null, StaleHandlesAfterDeviceLost = true,
        };
        var (sim, result, _) = await RunAsync(Standard, gpu, AlwaysDeviceLost, seed);

        var requestedOvershoot = result.Tests.Where(t => t.PointIndex >= 0)
            .Max(t => (t.FrequencyKhz - sim.Gpu.TrueLimitKhz(t.PointIndex)) / 1000.0);
        output.WriteLine($"max requested overshoot={requestedOvershoot:F0} MHz");
        Assert.InRange(requestedOvershoot, double.NegativeInfinity, 30);
        Assert.InRange(sim.Tester.MaxOvershootMhz, double.NegativeInfinity, 30);
        Assert.Equal(OptimizationStatus.Completed, result.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AfterTwoRealLimits_TheRemainingAnchorsArePredictedInsteadOfCrossed(int seed)
    {
        var gpu = new SimulatedGpuOptions { TrueDeltaMhz = MeasuredLikeDelta, PowerCapW = null, StaleHandlesAfterDeviceLost = true };
        var (sim, result, _) = await RunAsync(Standard, gpu, AlwaysDeviceLost, seed);

        var resets = result.Tests.Count(t => t.Outcome == StressOutcome.DeviceLost);
        Assert.True(resets <= 3, $"{resets} driver resets in one run");
        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.True(result.Anchors.Count >= 6, $"only {result.Anchors.Count} anchors");
        foreach (var a in result.Anchors)
            Assert.InRange(sim.Gpu.TrueLimitKhz(a.PointIndex) - a.MaxStableKhz, 0, 90_000);
    }

    /// <summary>The first test after a driver reset "crashes" because the driver is still hung, as on the RTX 5080.</summary>
    private sealed class CrashAfterResetTester(IStressTester inner) : IStressTester
    {
        private bool armed;

        public string AdapterName => inner.AdapterName;

        public Task CalibrateAsync(CancellationToken ct) => inner.CalibrateAsync(ct);

        public async Task<StressResult> RunAsync(StressRequest request, IProgress<StressProgress>? progress, CancellationToken ct)
        {
            if (armed)
            {
                armed = false;
                return new StressResult(StressOutcome.Crashed, TimeSpan.Zero, 0, 0, "driver still hung");
            }
            var result = await inner.RunAsync(request, progress, ct);
            armed = result.Outcome == StressOutcome.DeviceLost;
            return result;
        }

        public void Dispose() => inner.Dispose();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConfirmThatCrashesWhileTheDriverStillHangs_DoesNotDropTheAnchor(int seed)
    {
        var gpu = new SimulatedGpuOptions { TrueDeltaMhz = MeasuredLikeDelta, PowerCapW = null };
        var sim = Sim.Create(seed, gpu, AlwaysDeviceLost, wrapTester: t => new CrashAfterResetTester(t));
        var events = new EventCollector();
        var result = await sim.Optimizer.RunAsync(Standard, events, CancellationToken.None);

        var resetAnchors = result.Tests.Where(t => t.Kind == TestKind.Search && t.Outcome == StressOutcome.DeviceLost)
            .Select(t => t.AnchorIndex).Distinct().ToList();
        Assert.NotEmpty(resetAnchors);
        Assert.Contains(result.Tests, t => t.Outcome == StressOutcome.Crashed);
        foreach (var anchor in resetAnchors)
            Assert.Contains(result.Anchors, a => a.AnchorIndex == anchor);
        Assert.DoesNotContain(events.Of<Warning>(), w => w.Kind == WarningKind.AnchorUnconfirmed);
    }

    [Fact]
    public async Task Defaults_UseSmallSearchSteps()
    {
        Assert.True(Standard.GallopStepMhz <= 30, $"gallop step {Standard.GallopStepMhz} MHz");
        await Task.CompletedTask;
    }
}
