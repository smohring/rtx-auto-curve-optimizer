using RtxAutoCurveOptimizer.Optimization.Simulation;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

/// <summary>
/// A GPU that runs 130 MHz below the curve when hot (thermal bins) but the full curve value when cold, while hot
/// silicon also tolerates less. Hot-only tests prove just the derated clock; a cold burst may prove more, but never
/// above the cold limit.
/// </summary>
public sealed class ColdConfirmTests
{
    private static SimulatedGpuOptions Heated(double hotPenaltyMhz) => new()
    {
        ThermalDerateMhz = 130, HeatTimeConstant = TimeSpan.FromSeconds(15), HotLimitPenaltyMhz = hotPenaltyMhz,
    };

    [Fact]
    public async Task ColdBurst_ProvesClocksAboveTheHotMeasurementWhereTheGpuHoldsThemCold()
    {
        var sim = Sim.Create(1, Heated(hotPenaltyMhz: 100), new SimulatedStressOptions());
        var result = await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Quick), null, CancellationToken.None);

        Assert.NotEmpty(result.Anchors);
        Assert.Contains(result.Anchors, a => ProposalBuilder.ProvenKhz(a) >= a.AvgClockMhz * 1000 + 45_000);
        Assert.Contains(result.Tests, t => t.Kind == TestKind.ColdConfirm);
    }

    [Fact]
    public async Task ColdBurst_NeverProvesAClockAboveTheColdLimit()
    {
        foreach (var (seed, penalty) in new[] { (1, 0.0), (2, 0.0), (1, 100.0), (2, 100.0), (3, 100.0), (4, 130.0) })
        {
            var sim = Sim.Create(seed, Heated(penalty), new SimulatedStressOptions());
            var result = await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Quick), null, CancellationToken.None);

            foreach (var a in result.Anchors)
                Assert.True(ProposalBuilder.ProvenKhz(a) <= sim.Gpu.TrueLimitKhz(a.PointIndex),
                    $"seed {seed}, {a.VoltageUv / 1000} mV: proven {ProposalBuilder.ProvenKhz(a) / 1000} MHz, cold limit {sim.Gpu.TrueLimitKhz(a.PointIndex) / 1000} MHz");
        }
    }
}
