using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization.Simulation;
using Xunit.Abstractions;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

/// <summary>
/// The real RTX 5080 runs 45-60 MHz below the flat-curve target under load (thermal clock bins) at exactly the
/// anchor voltage and reports cap reasons without being capped. Such tests must count as valid.
/// </summary>
public class ThermalDerateTests(ITestOutputHelper output)
{
    private static readonly OptimizerSettings Quick = OptimizerSettings.For(OptimizationPreset.Quick);

    private static readonly SimulatedGpuOptions Derated = new() { ThermalDerateMhz = 60, SpuriousCapReasons = true };

    private static readonly SimulatedStressOptions NoPenalty = new() { PulsedPenaltyMhz = 0, VariablePenaltyMhz = 0 };

    /// <summary>Forwards to a simulated GPU but reports a higher voltage for samples near one curve voltage.</summary>
    private sealed class VoltageShiftGpu(SimulatedGpu inner, int nearUv, int shiftUv) : IGpuControl
    {
        public GpuIdentity Identity => inner.Identity;
        public bool CanWrite => inner.CanWrite;
        public StockCurve ReadStockCurve() => inner.ReadStockCurve();
        public CurveOffsets ReadOffsets() => inner.ReadOffsets();
        public void ApplyOffsets(CurveOffsets offsets) => inner.ApplyOffsets(offsets);
        public void Reinitialize() => inner.Reinitialize();
        public void Dispose() => inner.Dispose();

        public Telemetry ReadTelemetry()
        {
            var t = inner.ReadTelemetry();
            return Math.Abs(t.VoltageUv - nearUv) < 2_500 ? t with { VoltageUv = t.VoltageUv + shiftUv } : t;
        }
    }

    private async Task<(Sim Sim, OptimizationResult Result, EventCollector Events)> RunQuickAsync(
        int seed, SimulatedGpuOptions? gpuOptions = null)
    {
        var sim = Sim.Create(seed, gpuOptions ?? Derated, NoPenalty);
        var events = new EventCollector();
        var result = await sim.Optimizer.RunAsync(Quick, events, CancellationToken.None);
        output.WriteLine(TestFormat.Summary(result));
        output.WriteLine(TestFormat.AnchorTable(result, sim.Gpu));
        return (sim, result, events);
    }

    [Fact]
    public async Task ThermalDerate_StillProducesAllAnchors()
    {
        var (_, result, _) = await RunQuickAsync(1);

        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.Equal([0, 1, 2, 3], result.Anchors.Select(a => a.AnchorIndex));
    }

    [Fact]
    public async Task ThermalDerate_KeepsFullIntensityAndRaisesNoWarnings()
    {
        var (_, result, events) = await RunQuickAsync(1);

        Assert.All(result.Anchors, a => Assert.False(a.Limited));
        Assert.All(result.Anchors, a => Assert.Equal(1.0, a.Intensity));
        Assert.DoesNotContain(events.Of<Warning>(), w => w.Kind is WarningKind.AnchorSkipped or WarningKind.AnchorLimited
                                                          or WarningKind.IntensityReduced);
        Assert.All(result.Tests.Where(t => t.Kind is TestKind.Search or TestKind.Confirm && t.Outcome == StressOutcome.Passed),
            t => Assert.True(t.Valid, $"test {t.Sequence} at {t.FrequencyKhz} kHz judged invalid"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ThermalDerate_FindsTheTrueLimitWithinThreeStepsAndNeverAboveIt(int seed)
    {
        var (sim, result, _) = await RunQuickAsync(seed);

        Assert.Equal(4, result.Anchors.Count);
        foreach (var a in result.Anchors)
        {
            var truth = sim.Gpu.TrueLimitKhz(a.PointIndex);
            Assert.InRange(a.MaxStableKhz, truth - 3 * Quick.StepMhz * 1000, truth);
        }
    }

    [Fact]
    public async Task RealPowerCap_StillTriggersTheIntensityFallbackAndLimitsTheAnchor()
    {
        var options = new SimulatedGpuOptions
        {
            PowerCapW = 250, PowerLimitW = 250, ThermalDerateMhz = 60, SpuriousCapReasons = true,
        };

        var (_, result, events) = await RunQuickAsync(4, options);

        Assert.Equal(OptimizationStatus.Completed, result.Status);
        var limited = result.Anchors.Where(a => a.Limited).ToList();
        Assert.NotEmpty(limited);
        Assert.Contains(result.Anchors, a => a.Intensity < 1.0);
        Assert.Contains(events.Of<Warning>(), w => w.Kind == WarningKind.IntensityReduced);
        Assert.Contains(events.Of<Warning>(), w => w.Kind == WarningKind.AnchorLimited);
    }

    [Fact]
    public async Task VoltageAboveTheAnchor_SkipsThatAnchorWithAWarning()
    {
        var clock = new SimulatedClock();
        var gpu = new SimulatedGpu(clock, Derated, 1);
        var tester = new SimulatedStressTester(gpu, clock, 1, NoPenalty);
        var optimizer = new CurveOptimizer(new VoltageShiftGpu(gpu, 850_000, 50_000), tester, new InMemorySessionStore(), clock);
        var events = new EventCollector();

        var result = await optimizer.RunAsync(Quick, events, CancellationToken.None);

        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.Equal([1, 2, 3], result.Anchors.Select(a => a.AnchorIndex));
        Assert.Contains(events.Of<Warning>(), w => w.Kind == WarningKind.AnchorSkipped && w.Detail == "0");
        Assert.Single(result.Tests, t => t.AnchorIndex == 0);
    }
}
