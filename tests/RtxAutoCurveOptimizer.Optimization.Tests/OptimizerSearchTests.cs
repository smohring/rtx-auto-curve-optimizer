using System.Diagnostics;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization.Simulation;
using Xunit.Abstractions;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

public class OptimizerSearchTests(ITestOutputHelper output)
{
    private static readonly OptimizerSettings Standard = OptimizerSettings.For(OptimizationPreset.Standard);

    public static TheoryData<int> Seeds => new() { 1, 2, 3, 4, 5, 6, 7, 8 };

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Search_FindsEachTrueLimitWithinThreeStepsAndNeverAboveIt(int seed)
    {
        var sim = Sim.Create(seed);
        var pulsedPenalty = new SimulatedStressOptions().PulsedPenaltyMhz;

        var result = await sim.Optimizer.RunAsync(Standard, null, CancellationToken.None);

        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.Equal(7, result.Anchors.Count);
        foreach (var a in result.Anchors)
        {
            var truth = sim.Gpu.TrueLimitKhz(a.PointIndex);
            Assert.True(a.MaxStableKhz <= truth + 2_000,
                $"seed {seed} anchor {a.AnchorIndex}: stable {a.MaxStableKhz} above true limit {truth}");
            Assert.True(a.RecommendedKhz <= truth);
            Assert.True(a.MaxStableKhz >= truth - (3 * Standard.StepMhz + pulsedPenalty) * 1000,
                $"seed {seed} anchor {a.AnchorIndex}: stable {a.MaxStableKhz} far below true limit {truth}");
            Assert.Equal(Math.Min(a.MaxStableKhz, (int)Math.Round(a.AvgClockMhz * 1000)) - Standard.SafetyMarginMhz * 1000, a.RecommendedKhz);
        }
    }

    [Fact]
    public async Task Search_WithoutPulsedPenalty_LandsWithinThreeStepsOfTheLimit()
    {
        var sim = Sim.Create(11, stressOptions: new SimulatedStressOptions { PulsedPenaltyMhz = 0, VariablePenaltyMhz = 0 });

        var result = await sim.Optimizer.RunAsync(Standard, null, CancellationToken.None);

        foreach (var a in result.Anchors)
        {
            var truth = sim.Gpu.TrueLimitKhz(a.PointIndex);
            Assert.InRange(a.MaxStableKhz, truth - 3 * Standard.StepMhz * 1000, truth + 2_000);
        }
    }

    [Fact]
    public async Task Search_UsesFarFewerTestsThanANaiveLinearScan()
    {
        var sim = Sim.Create(5);

        var result = await sim.Optimizer.RunAsync(Standard, null, CancellationToken.None);

        var searchTests = result.Tests.Count(t => t.Kind == TestKind.Search);
        var confirmTests = result.Tests.Count(t => t.Kind == TestKind.Confirm);
        var naive = result.Anchors.Sum(a =>
            (sim.Gpu.TrueLimitKhz(a.PointIndex) - a.StockKhz) / 1000 / Standard.StepMhz + 1);
        output.WriteLine($"search tests: {searchTests}, confirm tests: {confirmTests}, naive 15 MHz scan: {naive}");
        output.WriteLine($"per anchor: {searchTests / (double)result.Anchors.Count:F1} search tests");

        Assert.True(searchTests * 3 < naive, $"{searchTests} search tests vs naive {naive}");
        Assert.True(searchTests + confirmTests < naive);
    }

    [Fact]
    public async Task Search_StartsEachAnchorAtOrAboveThePreviousStableFrequency()
    {
        var sim = Sim.Create(2);

        var result = await sim.Optimizer.RunAsync(Standard, null, CancellationToken.None);

        for (var k = 1; k < result.Anchors.Count; k++)
        {
            var previous = result.Anchors[k - 1];
            var current = result.Anchors[k];
            var first = result.Tests.First(t => t.Kind == TestKind.Search && t.AnchorIndex == current.AnchorIndex);
            Assert.True(first.FrequencyKhz >= previous.MaxStableKhz,
                $"anchor {k} starts at {first.FrequencyKhz}, previous stable {previous.MaxStableKhz}");
            Assert.True(current.MaxStableKhz >= previous.MaxStableKhz);
        }
    }

    [Fact]
    public async Task SameSeed_ProducesIdenticalRuns()
    {
        var first = await Sim.Create(9).Optimizer.RunAsync(Standard, null, CancellationToken.None);
        var second = await Sim.Create(9).Optimizer.RunAsync(Standard, null, CancellationToken.None);

        Assert.Equal(first.Anchors, second.Anchors);
        Assert.Equal(first.Tests, second.Tests);
        Assert.Equal(first.Elapsed, second.Elapsed);
    }

    [Fact]
    public async Task Search_FirstAnchorStartsAt150MhzOverStock()
    {
        var sim = Sim.Create(2);

        var result = await sim.Optimizer.RunAsync(Standard, null, CancellationToken.None);

        var first = result.Tests.First(t => t.Kind == TestKind.Search);
        Assert.Equal(result.Anchors[0].StockKhz + 150_000, first.FrequencyKhz);
    }

    [Fact]
    public async Task Search_ConfirmUsesSteadyThenPulsedAndRecordsTheSteadyAverages()
    {
        var sim = Sim.Create(2);
        var events = new EventCollector();

        var result = await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Quick), events, CancellationToken.None);

        var confirm = result.Tests.First(t => t.Kind == TestKind.Confirm && t.Passed);
        Assert.Equal(TimeSpan.FromSeconds(20), confirm.Duration);
        Assert.Equal(LoadPattern.Steady, confirm.Pattern);
        Assert.Equal(TimeSpan.FromSeconds(20), confirm.Elapsed);
        var anchor = result.Anchors.First(a => a.AnchorIndex == confirm.AnchorIndex && a.MaxStableKhz == confirm.FrequencyKhz);
        Assert.Equal(confirm.Throughput, anchor.Throughput);
        Assert.Equal(confirm.Telemetry!.AvgPowerW, anchor.AvgPowerW);
    }

    [Fact]
    public async Task PowerCap_TriggersIntensityFallbackAndMarksAnchorsLimited()
    {
        var sim = Sim.Create(4, new SimulatedGpuOptions { PowerCapW = 250, PowerLimitW = 250 });
        var events = new EventCollector();

        var result = await sim.Optimizer.RunAsync(Standard, events, CancellationToken.None);
        output.WriteLine(TestFormat.Summary(result));
        output.WriteLine(TestFormat.AnchorTable(result, sim.Gpu));

        Assert.Equal(OptimizationStatus.Completed, result.Status);
        var limited = result.Anchors.Where(a => a.Limited).ToList();
        Assert.NotEmpty(limited);
        Assert.All(limited, a => Assert.True(a.Intensity < 1.0));
        Assert.Contains(result.Anchors, a => !a.Limited && a.Intensity == 1.0);
        Assert.All(result.Anchors.Where(a => !a.Limited), a => Assert.True(a.VoltageUv < limited.Min(l => l.VoltageUv)));
        Assert.Contains(events.Of<Warning>(), w => w.Kind == WarningKind.IntensityReduced);
        Assert.Contains(events.Of<Warning>(), w => w.Kind == WarningKind.AnchorLimited);

        // An invalid pass at full intensity is repeated at the next lower intensity with the same frequency.
        var invalid = result.Tests.First(t => t.Kind == TestKind.Search && t.Outcome == StressOutcome.Passed && !t.Valid);
        Assert.Contains(result.Tests, t => t.AnchorIndex == invalid.AnchorIndex && t.FrequencyKhz == invalid.FrequencyKhz
                                           && t.Intensity < invalid.Intensity);
        foreach (var a in limited)
            Assert.True(a.MaxStableKhz <= sim.Gpu.TrueLimitKhz(a.PointIndex) + 2_000);
    }

    [Fact]
    public async Task DeviceLost_IsHandledAndTheRunContinues()
    {
        var sim = Sim.Create(6, stressOptions: new SimulatedStressOptions { DeviceLostFraction = 1.0 });
        var events = new EventCollector();

        var result = await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Quick), events, CancellationToken.None);

        var lost = result.Tests.Count(t => t.Outcome == StressOutcome.DeviceLost);
        Assert.True(lost > 0);
        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.Equal(4, result.Anchors.Count);
        Assert.Equal(lost, events.Of<Warning>().Count(w => w.Kind == WarningKind.DeviceLost));
        Assert.Equal(1 + lost, sim.Tester.CalibrationCount);
        foreach (var a in result.Anchors)
            Assert.True(a.MaxStableKhz <= sim.Gpu.TrueLimitKhz(a.PointIndex) + 2_000);
    }

    [Fact]
    public async Task FailureAtTheStockFrequencyAbortsWithAnError()
    {
        var sim = Sim.Create(1, new SimulatedGpuOptions { TrueDeltaMhz = mv => mv <= 900 ? -100 : 200, InitialOffsetKhz = 20_000 });

        var result = await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Quick), null, CancellationToken.None);

        Assert.Equal(OptimizationStatus.Failed, result.Status);
        Assert.Equal(OptimizationFailure.StockUnstable, result.Failure);
        Assert.All(sim.Offsets.OffsetsKhz, o => Assert.Equal(20_000, o));
    }

    [Fact]
    public async Task AnchorNeedingAnOffsetBeyondTheDriverRange_IsSkippedWithoutAnyTest()
    {
        var sim = Sim.Create(1, new SimulatedGpuOptions { OffsetLimitMhz = 1000 });
        var events = new EventCollector();
        var settings = Standard with { AnchorVoltagesMv = [825, 850, 900, 950] };

        var result = await sim.Optimizer.RunAsync(settings, events, CancellationToken.None);

        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.Equal(3, result.Anchors.Count);
        Assert.DoesNotContain(result.Anchors, a => a.AnchorIndex == 0);
        Assert.Contains(events.Of<Warning>(), w => w.Kind == WarningKind.AnchorSkipped && w.Detail == "0");
        Assert.DoesNotContain(events.Of<TestStarted>(), t => t.AnchorIndex == 0);
        Assert.DoesNotContain(result.Tests, t => t.AnchorIndex == 0);
    }

    [Fact]
    public async Task AnchorSkippedByThePreCheck_LeavesTheFollowingAnchorsStartingNormally()
    {
        var sim = Sim.Create(1, new SimulatedGpuOptions { OffsetLimitMhz = 1000 });
        var events = new EventCollector();
        var settings = Standard with { AnchorVoltagesMv = [800, 825, 850] };

        var result = await sim.Optimizer.RunAsync(settings, events, CancellationToken.None);

        Assert.Equal(2, events.Of<Warning>().Count(w => w.Kind == WarningKind.AnchorSkipped));
        var anchor = Assert.Single(result.Anchors);
        Assert.Equal(2, anchor.AnchorIndex);
    }

    [Fact]
    public async Task NotElevated_FailsWithoutTouchingTheGpu()
    {
        var sim = Sim.Create(1, new SimulatedGpuOptions { CanWrite = false });

        var result = await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Quick), null, CancellationToken.None);

        Assert.Equal(OptimizationFailure.GpuAccessDenied, result.Failure);
        Assert.Equal(0, sim.Gpu.ApplyCount);
        Assert.Empty(result.Tests);
    }

    [Fact]
    public async Task Events_FollowThePhaseOrderAndEveryTestIsAnnounced()
    {
        var sim = Sim.Create(3);
        var events = new EventCollector();

        var result = await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Quick), events, CancellationToken.None);

        var phases = events.Of<PhaseChanged>().Select(p => p.Phase).ToList();
        Assert.Equal(
            [OptimizerPhase.Preparing, OptimizerPhase.Calibrating, OptimizerPhase.WarmingUp, OptimizerPhase.MeasuringReferences,
                OptimizerPhase.SearchingAnchors, OptimizerPhase.BuildingProposals, OptimizerPhase.Validating,
                OptimizerPhase.Restoring, OptimizerPhase.Finished],
            phases);
        Assert.Equal(result.Tests.Count, events.Of<TestStarted>().Count());
        Assert.Equal(result.Tests.Count, events.Of<TestFinished>().Count());
        Assert.Equal(result.Anchors.Count, events.Of<AnchorCompleted>().Count());
        Assert.NotEmpty(events.Of<TelemetrySampled>());
        Assert.NotEmpty(events.Of<StressProgressed>());
        var etas = events.Of<EtaUpdated>().Select(e => e.Remaining).ToList();
        Assert.NotEmpty(etas);
        Assert.True(etas[0] > etas[^1]);
    }

    [Fact]
    public async Task Telemetry_IsSampledEvery250MsDuringATest()
    {
        var sim = Sim.Create(3);
        var events = new EventCollector();

        var result = await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Quick), events, CancellationToken.None);

        var reference = result.Tests.First(t => t.Kind == TestKind.ReferenceStock);
        Assert.Equal(40, reference.Telemetry!.SampleCount);
    }

    [Fact]
    public async Task FullStandardRun_CompletesInUnderTwoSecondsOfRealTime()
    {
        var sim = Sim.Create(1);
        var events = new EventCollector();
        var stopwatch = Stopwatch.StartNew();

        var result = await sim.Optimizer.RunAsync(Standard, events, CancellationToken.None);

        stopwatch.Stop();
        output.WriteLine($"real time: {stopwatch.ElapsedMilliseconds} ms");
        output.WriteLine(TestFormat.Summary(result));
        output.WriteLine(TestFormat.AnchorTable(result, sim.Gpu));
        foreach (var p in result.Proposals)
            output.WriteLine($"{p.Kind,-14} cap {p.CapAnchor.VoltageUv / 1000} mV  power {p.ExpectedPowerW:F0} W  perf {p.ExpectedRelativePerformance * 100:F1}%  validated {p.Validated}");

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(OptimizationStatus.Completed, result.Status);
        Assert.Equal(7, result.Anchors.Count);
        Assert.True(result.Recommended!.Validated);
        Assert.True(result.Elapsed > TimeSpan.FromMinutes(5));
    }
}
