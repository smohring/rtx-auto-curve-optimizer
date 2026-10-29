using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization.Simulation;
using Xunit.Abstractions;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

public class ProposalTests(ITestOutputHelper output)
{
    private static readonly StockCurve Stock = SimulatedGpu.CreateRtx5080StockCurve();

    private static AnchorResult A(int index, int mv, double relative, double perWatt, bool limited = false) =>
        CurveBuilderTests.Anchor(index, mv, 200, relative, perWatt, 200, limited);

    private static ProfileProposal Of(IReadOnlyList<ProfileProposal> proposals, ProposalKind kind) =>
        proposals.Single(p => p.Kind == kind);

    [Fact]
    public void Efficient_IsTheLowestAnchorWithAtLeastStockThroughput()
    {
        var anchors = new[] { A(0, 850, 0.80, 9), A(1, 900, 0.95, 8), A(2, 950, 1.02, 7), A(3, 1000, 1.03, 6), A(4, 1050, 1.04, 5) };

        var proposals = ProposalBuilder.Build(Stock, anchors);

        Assert.Equal(950_000, Of(proposals, ProposalKind.Efficient).CapAnchor.VoltageUv);
    }

    [Fact]
    public void Efficient_FallsBackToMaxThroughputWhenNoAnchorReachesStock()
    {
        var anchors = new[] { A(0, 850, 0.80, 9), A(1, 900, 0.97, 8), A(2, 950, 0.90, 7) };

        var proposals = ProposalBuilder.Build(Stock, anchors);

        Assert.Equal(900_000, Of(proposals, ProposalKind.Efficient).CapAnchor.VoltageUv);
    }

    [Fact]
    public void MaxEfficiency_PicksTheBestPerformancePerWatt()
    {
        var anchors = new[] { A(0, 850, 0.80, 9), A(1, 900, 0.97, 11), A(2, 950, 1.02, 7) };

        var proposals = ProposalBuilder.Build(Stock, anchors);

        Assert.Equal(900_000, Of(proposals, ProposalKind.MaxEfficiency).CapAnchor.VoltageUv);
    }

    [Fact]
    public void Performance_PicksMaxThroughputAndBreaksTiesTowardsLowerVoltage()
    {
        var anchors = new[] { A(0, 850, 0.80, 9), A(1, 950, 1.05, 7), A(2, 1000, 1.05, 6), A(3, 1050, 1.01, 5) };

        var proposals = ProposalBuilder.Build(Stock, anchors);

        Assert.Equal(950_000, Of(proposals, ProposalKind.Performance).CapAnchor.VoltageUv);
    }

    [Fact]
    public void LimitedAnchors_AreOnlyUsedWhenNothingElseExists()
    {
        var mixed = new[] { A(0, 850, 0.90, 9), A(1, 1000, 1.10, 12, limited: true) };
        var onlyLimited = new[] { A(0, 1000, 1.10, 12, limited: true) };

        Assert.Equal(850_000, Of(ProposalBuilder.Build(Stock, mixed), ProposalKind.Efficient).CapAnchor.VoltageUv);
        Assert.Equal(1_000_000, Of(ProposalBuilder.Build(Stock, onlyLimited), ProposalKind.Efficient).CapAnchor.VoltageUv);
    }

    [Fact]
    public void EmptyAnchors_YieldNoProposals() => Assert.Empty(ProposalBuilder.Build(Stock, []));

    [Fact]
    public void Proposals_CarryTheFinalCurveAndTheCapAnchorFigures()
    {
        var anchors = new[] { A(0, 850, 0.90, 9), A(1, 900, 1.02, 8) };

        var efficient = Of(ProposalBuilder.Build(Stock, anchors), ProposalKind.Efficient);

        Assert.Equal(CurveBuilder.BuildFinal(Stock, anchors, anchors[1], 0).OffsetsKhz, efficient.Offsets.OffsetsKhz);
        Assert.Equal(anchors[1].AvgPowerW, efficient.ExpectedPowerW);
        Assert.Equal(ProposalBuilder.PredictedRelative(anchors[1], 0), efficient.ExpectedRelativePerformance);
        Assert.True(efficient.ExpectedRelativePerformance < anchors[1].ThroughputRelative);
        Assert.False(efficient.Validated);
    }

    [Fact]
    public void Merge_CombinesIdenticalProposalsIntoOneGroupWithAllKinds()
    {
        var anchors = new[] { A(0, 850, 0.80, 9), A(1, 900, 1.02, 8), A(2, 950, 1.05, 12) };
        var proposals = ProposalBuilder.Build(Stock, anchors);

        var groups = ProposalGroup.Merge(proposals);

        Assert.Equal(2, groups.Count);
        Assert.Equal(ProposalKind.Efficient, groups[0].Primary.Kind);
        Assert.Single(groups[0].Members);
        Assert.Equal([ProposalKind.MaxEfficiency, ProposalKind.Performance], groups[1].Members.Select(m => m.Kind));
    }

    [Fact]
    public void Merge_KeepsEfficientAndPerformanceTogetherAndMarksTheGroupRecommended()
    {
        var anchors = new[] { A(0, 850, 0.80, 9), A(1, 900, 1.05, 12) };
        var proposals = ProposalBuilder.Build(Stock, anchors);

        var group = Assert.Single(ProposalGroup.Merge(proposals));

        Assert.Equal(3, group.Members.Count);
        Assert.True(group.IsRecommended);
        Assert.True(group.Contains(ProposalKind.Performance));
        Assert.False(group.Contains(ProposalKind.Custom));
    }

    [Fact]
    public void Merge_KeepsProposalsApartWhenOffsetsDifferDespiteTheSameCap()
    {
        var anchors = new[] { A(0, 850, 0.90, 9), A(1, 900, 1.02, 8) };
        var plain = ProposalBuilder.Create(Stock, anchors, ProposalKind.Efficient, anchors[1], 0);
        var margin = ProposalBuilder.Create(Stock, anchors, ProposalKind.Custom, anchors[1], 15);

        var groups = ProposalGroup.Merge([plain, margin]);

        Assert.Equal(2, groups.Count);
        Assert.False(groups[1].IsRecommended);
    }

    [Fact]
    public void Merge_GroupIsValidatedWhenAnyMemberIs()
    {
        var anchors = new[] { A(0, 850, 0.90, 9), A(1, 900, 1.02, 12) };
        var efficient = ProposalBuilder.Create(Stock, anchors, ProposalKind.Efficient, anchors[1], 0);
        var performance = ProposalBuilder.Create(Stock, anchors, ProposalKind.Performance, anchors[1], 0, validated: true);

        Assert.True(Assert.Single(ProposalGroup.Merge([efficient, performance])).Validated);
        Assert.False(Assert.Single(ProposalGroup.Merge([efficient])).Validated);
    }

    [Fact]
    public void Merge_OfNothingIsEmpty() => Assert.Empty(ProposalGroup.Merge([]));

    [Fact]
    public async Task SimulatedRun_EfficientIsTheLowestAnchorAtOrAboveStockThroughput()
    {
        var sim = Sim.Create(1);

        var result = await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Standard), null, CancellationToken.None);

        var expected = result.Anchors.OrderBy(a => a.VoltageUv).First(a => a.ThroughputRelative >= 1.0);
        output.WriteLine($"Efficient cap: {expected.VoltageUv / 1000} mV, relative performance {expected.ThroughputRelative:P1}");
        Assert.Contains(result.Anchors, a => a.ThroughputRelative >= 1.0);
        Assert.Contains(result.Anchors, a => a.ThroughputRelative < 1.0);
        Assert.Equal(expected.AnchorIndex, result.Recommended!.CapAnchor.AnchorIndex);
        Assert.Equal(expected.AnchorIndex, result.Proposals.Single(p => p.Kind == ProposalKind.Efficient).CapAnchor.AnchorIndex);
        Assert.Equal(
            result.Anchors.MaxBy(a => a.PerfPerWatt)!.AnchorIndex,
            result.Proposals.Single(p => p.Kind == ProposalKind.MaxEfficiency).CapAnchor.AnchorIndex);
        Assert.Equal(
            result.Anchors.MaxBy(a => a.ThroughputRelative)!.AnchorIndex,
            result.Proposals.Single(p => p.Kind == ProposalKind.Performance).CapAnchor.AnchorIndex);
        Assert.Equal(1, result.Proposals.Count(p => p.Validated));
        Assert.True(result.Recommended.Validated);
    }
}
