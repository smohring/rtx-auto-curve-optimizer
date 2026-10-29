using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization.Simulation;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

/// <summary>
/// Reproduces the curve that crashed Battlefield right away: tests ran 100-150 MHz below the curve value
/// (thermal clock bins), but the curve value was recorded as stable, so a cold GPU ran far above anything tested.
/// </summary>
public sealed class ProvenClockTests
{
    private static readonly StockCurve Stock = SimulatedGpu.CreateRtx5080StockCurve();

    /// <summary>Anchor as measured on the RTX 5080: curve value <paramref name="curveMhz"/>, measured clock under load <paramref name="measuredMhz"/>.</summary>
    private static AnchorResult Measured(int index, int mv, int curveMhz, int measuredMhz, double relative, double power)
    {
        var p = CurveBuilder.NearestPointIndex(Stock, mv);
        var point = Stock.Points[p];
        return new AnchorResult(index, p, point.VoltageUv, point.StockKhz, curveMhz * 1000, (curveMhz - 30) * 1000,
            power, measuredMhz, point.VoltageUv, 63, 1000 * relative, relative, 1000 * relative / power, false, 1.0);
    }

    private static SessionState Session(params AnchorResult[] anchors)
    {
        var now = DateTimeOffset.UnixEpoch;
        var settings = OptimizerSettings.For(OptimizationPreset.Standard);
        var original = new CurveOffsets([.. Enumerable.Repeat(300_000, Stock.Points.Count)]);
        var proposals = ProposalBuilder.Build(Stock, anchors);
        return new SessionState("t", SessionStatus.Completed, now, now,
            new GpuIdentity("GPU", "621.01", 0x2C02, 0x209510DE, 0xA1, 1), Stock, original, settings,
            null, null, [], [.. anchors], [.. proposals], null, true, OptimizationFailure.None, null);
    }

    /// <summary>The run on the RTX 5080 (session 20261025-212251), first four anchors.</summary>
    private static readonly AnchorResult[] RealRun =
    [
        Measured(0, 850, 2745, 2632, 0.965, 187),
        Measured(1, 875, 2850, 2752, 1.004, 208),
        Measured(2, 900, 2955, 2820, 1.017, 224),
        Measured(3, 925, 3037, 2925, 1.043, 239),
    ];

    [Fact]
    public void Recompute_RecommendsAtMostTheMeasuredClockMinusTheMargin()
    {
        var result = SessionRecompute.Recompute(Session(RealRun));

        foreach (var a in result.Anchors)
            Assert.True(a.RecommendedKhz <= (int)(a.AvgClockMhz * 1000) - 30_000,
                $"{a.VoltageUv / 1000} mV: recommended {a.RecommendedKhz / 1000} MHz, measured {a.AvgClockMhz} MHz");
    }

    [Fact]
    public void Recompute_CurveNeverExceedsTheUsersProvenOffsetBelowTheFirstAnchor()
    {
        var result = SessionRecompute.Recompute(Session(RealRun));

        var firstVoltage = result.Anchors.Min(a => a.VoltageUv);
        foreach (var proposal in result.Proposals)
            for (var j = 0; j < Stock.Points.Count && Stock.Points[j].VoltageUv < firstVoltage; j++)
                Assert.True(proposal.Offsets.OffsetsKhz[j] <= 300_000,
                    $"{proposal.Kind}: {Stock.Points[j].VoltageUv / 1000} mV offset {proposal.Offsets.OffsetsKhz[j] / 1000} MHz");
    }

    [Fact]
    public void Recompute_ExpectedPerformanceAccountsForTheThermalDropBelowTheNewCurve()
    {
        var result = SessionRecompute.Recompute(Session(RealRun));

        foreach (var p in result.Proposals)
            Assert.True(p.ExpectedRelativePerformance < p.CapAnchor.ThroughputRelative - 0.02,
                $"{p.Kind}: expected {p.ExpectedRelativePerformance:P1} vs measured {p.CapAnchor.ThroughputRelative:P1}");
        Assert.All(result.Proposals, p => Assert.False(p.Validated));
    }

    [Fact]
    public void Recompute_WithALargerMargin_LowersEveryRecommendationByTheDifference()
    {
        var normal = SessionRecompute.Recompute(Session(RealRun));
        var lowered = SessionRecompute.Recompute(Session(RealRun), 60);

        Assert.Equal(60, lowered.Settings.SafetyMarginMhz);
        Assert.All(normal.Anchors.Zip(lowered.Anchors), p => Assert.Equal(p.First.RecommendedKhz - 30_000, p.Second.RecommendedKhz));
    }

    [Fact]
    public async Task Run_WithThermalDerate_RecommendsAtMostTheMeasuredClockMinusTheMargin()
    {
        var sim = Sim.Create(1, new SimulatedGpuOptions { ThermalDerateMhz = 100 }, new SimulatedStressOptions());
        var result = await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Quick), null, CancellationToken.None);

        Assert.NotEmpty(result.Anchors);
        foreach (var a in result.Anchors)
            Assert.True(a.RecommendedKhz <= (int)(a.AvgClockMhz * 1000) - 30_000 + 1000,
                $"{a.VoltageUv / 1000} mV: recommended {a.RecommendedKhz / 1000} MHz, measured {a.AvgClockMhz:F0} MHz");
    }

    [Fact]
    public void Shifted_RaisesEveryAnchorOnlyUpToTheClockMeasuredUnderLoad()
    {
        var session = SessionRecompute.Recompute(Session(RealRun));
        var efficient = session.Proposals.Single(p => p.Kind == ProposalKind.Efficient);
        var cap = efficient.CapAnchor;

        var raised = ProposalBuilder.Shifted(Stock, session.Anchors, efficient, 500, session.OriginalOffsets);

        foreach (var a in session.Anchors.Where(a => a.VoltageUv <= cap.VoltageUv))
            Assert.Equal(Math.Min(a.MaxStableKhz, (int)Math.Round(a.AvgClockMhz * 1000)), Stock.Points[a.PointIndex].StockKhz + raised.Offsets.OffsetsKhz[a.PointIndex]);
        var lowered = ProposalBuilder.Shifted(Stock, session.Anchors, efficient, -30, session.OriginalOffsets);
        Assert.Equal(cap.RecommendedKhz - 30_000, Stock.Points[cap.PointIndex].StockKhz + lowered.Offsets.OffsetsKhz[cap.PointIndex]);
    }

    [Fact]
    public void Limited_AboveTheCap_MovesTheCapToTheLowestAnchorThatReachesTheLimit()
    {
        var session = SessionRecompute.Recompute(Session(RealRun));
        var proposal = session.Proposals.Single(p => p.Kind == ProposalKind.MaxEfficiency);
        var above = session.Anchors.Where(a => a.VoltageUv > proposal.CapAnchor.VoltageUv).OrderBy(a => a.VoltageUv).First();
        int Top(ProfileProposal p) => Stock.Points.Select((pt, j) => pt.StockKhz + p.Offsets.OffsetsKhz[j]).Max() / 1000;
        int TopVoltage(ProfileProposal p) => Stock.Points.Where((pt, j) => pt.StockKhz + p.Offsets.OffsetsKhz[j] >= Top(p) * 1000).Min(pt => pt.VoltageUv);
        var limit = (proposal.CapAnchor.RecommendedKhz + above.RecommendedKhz) / 2000;

        var raised = ProposalBuilder.Limited(Stock, session.Anchors, proposal, 0, limit, session.OriginalOffsets);
        var cut = ProposalBuilder.Limited(Stock, session.Anchors, proposal, 0, limit - 100, session.OriginalOffsets);

        Assert.Equal(limit, Top(raised));
        Assert.True(TopVoltage(raised) > proposal.CapAnchor.VoltageUv && TopVoltage(raised) <= above.VoltageUv);
        foreach (var a in session.Anchors)
            Assert.True(Stock.Points[a.PointIndex].StockKhz + raised.Offsets.OffsetsKhz[a.PointIndex] <= ProposalBuilder.ProvenKhz(a));
        Assert.Equal(limit - 100, Top(cut));
    }

    [Fact]
    public void Estimate_OfAnUnchangedProposal_MatchesItsOwnFiguresAndDropsWhenTheCurveIsCut()
    {
        var session = SessionRecompute.Recompute(Session(RealRun));
        var efficient = session.Proposals.Single(p => p.Kind == ProposalKind.Efficient);

        var same = ProposalBuilder.Estimate(Stock, session.Anchors, efficient.Offsets);
        var cut = ProposalBuilder.Estimate(Stock, session.Anchors,
            ProposalBuilder.Limited(Stock, session.Anchors, efficient, 0, ProposalBuilder.PeakMhz(Stock, efficient) - 150, session.OriginalOffsets).Offsets);

        Assert.Equal(efficient.CapAnchor.VoltageUv, same.CapVoltageUv);
        Assert.Equal(efficient.ExpectedPowerW, same.PowerW, 1);
        Assert.Equal(efficient.ExpectedRelativePerformance, same.RelativePerformance, 3);
        Assert.True(cut.PowerW < same.PowerW && cut.RelativePerformance < same.RelativePerformance);
    }

    [Fact]
    public void Uniform_RaisesEveryAnchorByTheSameOffsetCappedAtTheStockBoostClockAndNeverAboveARecommendation()
    {
        var session = SessionRecompute.Recompute(Session(RealRun));
        const int stockBoostMhz = 2771;
        var anchors = session.Anchors;
        // Anchors recommended at or above the stock boost clock only carry the flat part and do not limit the offset.
        var offset = anchors.Where(a => a.RecommendedKhz < stockBoostMhz * 1000).Min(a => a.RecommendedKhz - a.StockKhz);

        var uniform = ProposalBuilder.Uniform(Stock, anchors, stockBoostMhz);

        Assert.Equal(ProposalKind.Uniform, uniform.Kind);
        Assert.Equal(stockBoostMhz, ProposalBuilder.PeakMhz(Stock, uniform));
        foreach (var a in anchors)
        {
            var khz = Stock.Points[a.PointIndex].StockKhz + uniform.Offsets.OffsetsKhz[a.PointIndex];
            Assert.True(khz <= a.RecommendedKhz);
            Assert.Equal(Math.Min(a.StockKhz + offset, stockBoostMhz * 1000), khz);
        }

        // Like the manual method, the offset also applies below the first anchor (the session started at +300 there).
        var low = CurveBuilder.NearestPointIndex(Stock, 800);
        Assert.Equal(offset, uniform.Offsets.OffsetsKhz[low]);
    }

    [Fact]
    public void Uniform_CapCanMoveRightKeepingTheOffsetUntilTheFlatPartWouldPassAProvenClock()
    {
        var session = SessionRecompute.Recompute(Session(RealRun));
        const int stockBoostMhz = 2771;
        var anchors = session.Anchors;
        var atBoost = ProposalBuilder.Uniform(Stock, anchors, stockBoostMhz);
        var maxCap = ProposalBuilder.UniformMaxCapMhz(Stock, anchors, stockBoostMhz, 0);

        var moved = ProposalBuilder.Uniform(Stock, anchors, stockBoostMhz, capMhz: maxCap);

        Assert.True(maxCap > stockBoostMhz);
        Assert.Equal(maxCap, ProposalBuilder.PeakMhz(Stock, moved));
        var low = CurveBuilder.NearestPointIndex(Stock, 800);
        Assert.Equal(atBoost.Offsets.OffsetsKhz[low], moved.Offsets.OffsetsKhz[low]);
        foreach (var a in anchors)
            Assert.True(Stock.Points[a.PointIndex].StockKhz + moved.Offsets.OffsetsKhz[a.PointIndex] <= ProposalBuilder.ProvenKhz(a));
        Assert.True(ProposalBuilder.Estimate(Stock, anchors, moved.Offsets).CapVoltageUv <= anchors.Max(a => a.VoltageUv));
    }
}
