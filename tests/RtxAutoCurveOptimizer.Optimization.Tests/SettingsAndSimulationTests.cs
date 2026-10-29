using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization.Simulation;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

public class SettingsAndSimulationTests
{
    [Fact]
    public void Presets_MatchTheSpecTable()
    {
        var quick = OptimizerSettings.For(OptimizationPreset.Quick);
        var standard = OptimizerSettings.For(OptimizationPreset.Standard);
        var thorough = OptimizerSettings.For(OptimizationPreset.Thorough);

        Assert.Equal([850, 900, 950, 1000], quick.AnchorVoltagesMv);
        Assert.Equal(
            (20, 10, 6, 20, 90),
            ((int)quick.WarmupDuration.TotalSeconds, (int)quick.ReferenceDuration.TotalSeconds,
                (int)quick.SearchTestDuration.TotalSeconds, (int)quick.ConfirmTestDuration.TotalSeconds,
                (int)quick.ValidationDuration.TotalSeconds));
        Assert.Equal([850, 875, 900, 925, 950, 1000, 1050], standard.AnchorVoltagesMv);
        Assert.Equal(
            (30, 15, 10, 45, 180),
            ((int)standard.WarmupDuration.TotalSeconds, (int)standard.ReferenceDuration.TotalSeconds,
                (int)standard.SearchTestDuration.TotalSeconds, (int)standard.ConfirmTestDuration.TotalSeconds,
                (int)standard.ValidationDuration.TotalSeconds));
        Assert.Equal(11, thorough.AnchorVoltagesMv.Count);
        Assert.Equal(850, thorough.AnchorVoltagesMv[0]);
        Assert.Equal(1100, thorough.AnchorVoltagesMv[^1]);
        Assert.Equal(
            (45, 20, 20, 120, 600),
            ((int)thorough.WarmupDuration.TotalSeconds, (int)thorough.ReferenceDuration.TotalSeconds,
                (int)thorough.SearchTestDuration.TotalSeconds, (int)thorough.ConfirmTestDuration.TotalSeconds,
                (int)thorough.ValidationDuration.TotalSeconds));
        Assert.Equal((15, 30, 30, 600), (standard.StepMhz, standard.GallopStepMhz, standard.SafetyMarginMhz, standard.MaxOffsetMhz));
    }

    [Fact]
    public void EstimateDuration_AssumesEightTestsPerAnchorPlusTheColdConfirm()
    {
        var quick = OptimizerSettings.For(OptimizationPreset.Quick);

        // 20 warm-up + 2 x 10 reference + 4 x (8 x 6 + 20 + 80 cold) + 90 validation
        Assert.Equal(TimeSpan.FromSeconds(722), quick.EstimateDuration());
        Assert.Equal(TimeSpan.FromSeconds(402), (quick with { ColdConfirm = false }).EstimateDuration());
    }

    [Fact]
    public void Validate_RejectsUnusableSettings()
    {
        var good = OptimizerSettings.For(OptimizationPreset.Quick);

        Assert.Throws<ArgumentException>(() => (good with { AnchorVoltagesMv = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { StepMhz = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { GallopStepMhz = 5 }).Validate());
    }

    [Fact]
    public async Task SimulatedClock_FiresTimersOnAdvanceAndJumpsWhenIdle()
    {
        var clock = new SimulatedClock();
        var fired = 0;
        using var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        // Idle clock: a new timer jumps time to its due time and fires on the pool.
        await Task.Delay(TimeSpan.FromSeconds(5), clock);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);

        clock.EnterBusy();
        var busyFired = 0;
        using var busyTimer = clock.CreateTimer(_ => busyFired++, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(0, busyFired);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, busyFired);
        clock.ExitBusy();
    }

    [Fact]
    public void SimulatedGpu_RunsAtTheFlatCurvePointUnderLoad()
    {
        var clock = new SimulatedClock();
        var gpu = new SimulatedGpu(clock);
        var i = CurveBuilder.NearestPointIndex(gpu.Stock, 900);
        var freq = gpu.Stock.Points[i].StockKhz + 200_000;
        gpu.ApplyOffsets(CurveBuilder.BuildFlatTest(gpu.Stock, i, freq));

        var op = gpu.OperatingPoint(LoadPattern.Steady, 1.0);

        Assert.Equal(i, op.PointIndex);
        Assert.Equal(freq, op.ClockKhz);
        Assert.False(op.PowerCapped);
    }

    [Fact]
    public void SimulatedGpu_PowerCapLowersTheClockAndReportsTheReason()
    {
        var clock = new SimulatedClock();
        var gpu = new SimulatedGpu(clock, new SimulatedGpuOptions { PowerCapW = 200 });
        var i = CurveBuilder.NearestPointIndex(gpu.Stock, 1000);
        var freq = gpu.Stock.Points[i].StockKhz + 200_000;
        gpu.ApplyOffsets(CurveBuilder.BuildFlatTest(gpu.Stock, i, freq));

        var full = gpu.OperatingPoint(LoadPattern.Steady, 1.0);
        var light = gpu.OperatingPoint(LoadPattern.Steady, 0.4);

        Assert.True(full.PowerCapped);
        Assert.True(full.ClockKhz < freq);
        Assert.True(full.PowerW <= 200);
        Assert.False(light.PowerCapped);
        Assert.Equal(freq, light.ClockKhz);
    }

    [Fact]
    public async Task SimulatedStressTester_RequiresCalibrationAndFailsAboveTheLimit()
    {
        var clock = new SimulatedClock();
        var gpu = new SimulatedGpu(clock);
        using var tester = new SimulatedStressTester(gpu, clock, seed: 3);
        var request = new StressRequest(TimeSpan.FromSeconds(30), LoadPattern.Steady, 1.0);
        Assert.Throws<InvalidOperationException>(() => { _ = tester.RunAsync(request, null, CancellationToken.None); });
        await tester.CalibrateAsync(CancellationToken.None);

        var i = CurveBuilder.NearestPointIndex(gpu.Stock, 900);
        gpu.ApplyOffsets(CurveBuilder.BuildFlatTest(gpu.Stock, i, gpu.TrueLimitKhz(i) - 20_000));
        var below = await tester.RunAsync(request, null, CancellationToken.None);
        gpu.ApplyOffsets(CurveBuilder.BuildFlatTest(gpu.Stock, i, gpu.TrueLimitKhz(i) + 60_000));
        var above = await tester.RunAsync(request, null, CancellationToken.None);

        Assert.Equal(StressOutcome.Passed, below.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(30), below.Elapsed);
        Assert.NotEqual(StressOutcome.Passed, above.Outcome);
        Assert.True(above.Elapsed < TimeSpan.FromSeconds(30));
    }
}
