using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization.Simulation;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

public class CurveBuilderTests
{
    private static readonly StockCurve Stock = SimulatedGpu.CreateRtx5080StockCurve();

    internal static AnchorResult Anchor(int index, int mv, int recommendedDeltaMhz, double relative = 1.0, double perWatt = 10,
        double power = 200, bool limited = false)
    {
        var p = CurveBuilder.NearestPointIndex(Stock, mv);
        var point = Stock.Points[p];
        var recommended = point.StockKhz + recommendedDeltaMhz * 1000;
        return new AnchorResult(index, p, point.VoltageUv, point.StockKhz, recommended + 30_000, recommended,
            power, (recommended + 30_000) / 1000.0, point.VoltageUv, 60, 1000 * relative, relative, perWatt, limited, 1.0);
    }

    private static int Frequency(CurveOffsets offsets, int pointIndex) =>
        Stock.Points[pointIndex].StockKhz + offsets.OffsetsKhz[pointIndex];

    [Fact]
    public void SimulatedStockCurve_ResemblesRtx5080()
    {
        Assert.Equal(127, Stock.Points.Count);
        Assert.Equal(450_000, Stock.Points[0].VoltageUv);
        Assert.Equal(1_240_000, Stock.Points[^1].VoltageUv);
        Assert.Equal(180_000, Stock.Points[0].StockKhz);
        Assert.InRange(Stock.Points[CurveBuilder.NearestPointIndex(Stock, 1000)].StockKhz, 2_715_000, 2_730_000);
        for (var j = 1; j < Stock.Points.Count; j++)
        {
            Assert.True(Stock.Points[j].VoltageUv > Stock.Points[j - 1].VoltageUv);
            Assert.True(Stock.Points[j].StockKhz >= Stock.Points[j - 1].StockKhz);
        }
    }

    [Fact]
    public void BuildFlatTest_IsZeroBelowAndFlatFromThePoint()
    {
        var i = CurveBuilder.NearestPointIndex(Stock, 900);
        const int freq = 2_600_000;

        var offsets = CurveBuilder.BuildFlatTest(Stock, i, freq);

        Assert.Equal(Stock.Points.Count, offsets.OffsetsKhz.Count);
        for (var j = 0; j < i; j++) Assert.Equal(0, offsets.OffsetsKhz[j]);
        for (var j = i; j < Stock.Points.Count; j++) Assert.Equal(freq, Frequency(offsets, j));
    }

    [Fact]
    public void BuildFlatTest_RejectsPointOutsideTheCurve()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CurveBuilder.BuildFlatTest(Stock, Stock.Points.Count, 2_000_000));
    }

    [Fact]
    public void BuildFinal_IsMonotonicAndFlatAboveTheCap()
    {
        var anchors = new[] { Anchor(0, 850, 250), Anchor(1, 900, 230), Anchor(2, 1000, 190), Anchor(3, 1050, 150) };
        var cap = anchors[2];

        var offsets = CurveBuilder.BuildFinal(Stock, anchors, cap, 0);

        for (var j = 1; j < Stock.Points.Count; j++)
            Assert.True(Frequency(offsets, j) >= Frequency(offsets, j - 1), $"point {j}");
        for (var j = 0; j < Stock.Points.Count; j++)
        {
            Assert.True(Frequency(offsets, j) <= cap.RecommendedKhz);
            if (Stock.Points[j].VoltageUv > cap.VoltageUv) Assert.Equal(cap.RecommendedKhz, Frequency(offsets, j));
        }
        Assert.Equal(cap.RecommendedKhz, Frequency(offsets, cap.PointIndex));
    }

    [Fact]
    public void BuildFinal_InterpolatesDeltaBetweenAnchors()
    {
        var a = Anchor(0, 850, 260);
        var b = Anchor(1, 950, 160);
        var mid = CurveBuilder.NearestPointIndex(Stock, 900);

        var offsets = CurveBuilder.BuildFinal(Stock, [a, b], b, 0);

        var fraction = (Stock.Points[mid].VoltageUv - a.VoltageUv) / (double)(b.VoltageUv - a.VoltageUv);
        var expected = 260_000 + (160_000 - 260_000) * fraction;
        Assert.Equal(expected, offsets.OffsetsKhz[mid], 1.0);
        Assert.Equal(260_000, offsets.OffsetsKhz[a.PointIndex]);
        Assert.Equal(160_000, offsets.OffsetsKhz[b.PointIndex]);
    }

    [Fact]
    public void BuildFinal_UsesTheFirstAnchorDeltaBelowIt()
    {
        var a = Anchor(0, 850, 260);
        var b = Anchor(1, 950, 160);

        var offsets = CurveBuilder.BuildFinal(Stock, [a, b], b, 0);

        for (var j = 0; j <= a.PointIndex; j++) Assert.Equal(260_000, offsets.OffsetsKhz[j]);
    }

    [Fact]
    public void BuildFinal_SubtractsTheExtraMarginAtEveryAnchor()
    {
        var a = Anchor(0, 850, 260);
        var b = Anchor(1, 950, 160);

        var plain = CurveBuilder.BuildFinal(Stock, [a, b], b, 0);
        var margin = CurveBuilder.BuildFinal(Stock, [a, b], b, 15);

        Assert.Equal(plain.OffsetsKhz[a.PointIndex] - 15_000, margin.OffsetsKhz[a.PointIndex]);
        Assert.Equal(plain.OffsetsKhz[b.PointIndex] - 15_000, margin.OffsetsKhz[b.PointIndex]);
    }

    [Fact]
    public void BuildFinal_IgnoresAnchorsAboveTheCap()
    {
        var a = Anchor(0, 850, 260);
        var b = Anchor(1, 950, 160);
        var c = Anchor(2, 1050, 40);

        var withC = CurveBuilder.BuildFinal(Stock, [a, b, c], b, 0);
        var without = CurveBuilder.BuildFinal(Stock, [a, b], b, 0);

        Assert.Equal(without.OffsetsKhz, withC.OffsetsKhz);
    }

    [Fact]
    public void BuildFinal_ClampsOffsetsToPlusMinus1000Mhz()
    {
        var huge = Anchor(0, 850, 1500);
        var low = Anchor(1, 1000, -1500);

        var up = CurveBuilder.BuildFinal(Stock, [huge], huge, 0);
        var down = CurveBuilder.BuildFinal(Stock, [low], low, 0);

        Assert.All(up.OffsetsKhz, o => Assert.InRange(o, -1_000_000, 1_000_000));
        Assert.All(down.OffsetsKhz, o => Assert.InRange(o, -1_000_000, 1_000_000));
        Assert.Equal(1_000_000, up.OffsetsKhz[huge.PointIndex]);
    }

    [Fact]
    public void BuildFinal_EnforcesNonDecreasingFrequenciesWhenAnchorsDecrease()
    {
        var a = Anchor(0, 900, 100);
        var b = Anchor(1, 1000, -400);

        var offsets = CurveBuilder.BuildFinal(Stock, [a, b], b, 0);

        for (var j = 1; j < Stock.Points.Count; j++)
            Assert.True(Frequency(offsets, j) >= Frequency(offsets, j - 1));
    }

    [Fact]
    public void MaxRaise_IsTheLargestGapToTheMeasuredClockUpToTheCapRoundedUpToWholeSteps()
    {
        var p850 = CurveBuilder.NearestPointIndex(Stock, 850); var p900 = CurveBuilder.NearestPointIndex(Stock, 900); var p950 = CurveBuilder.NearestPointIndex(Stock, 950);
        AnchorResult At(int index, int point, int maxStableKhz, int recommendedKhz) =>
            Anchor(index, Stock.Points[point].VoltageUv / 1000, 0) with { MaxStableKhz = maxStableKhz + 100_000, AvgClockMhz = maxStableKhz / 1000.0, RecommendedKhz = recommendedKhz };
        var a = At(0, p850, 2_745_000, 2_602_000);   // gap 143
        var b = At(1, p900, 2_955_000, 2_791_000);   // gap 164
        var c = At(2, p950, 3_100_000, 3_090_000);   // gap 10, above the cap below

        Assert.Equal(165, CurveBuilder.MaxRaiseMhz([a, b, c], b));
        Assert.Equal(150, CurveBuilder.MaxRaiseMhz([a, b, c], a));
    }

    [Fact]
    public void MaxRaise_ForAnArbitraryCurve_StopsAtTheLowestGapToTheMeasuredClock()
    {
        var p850 = CurveBuilder.NearestPointIndex(Stock, 850); var p900 = CurveBuilder.NearestPointIndex(Stock, 900);
        var a = Anchor(0, 850, 0) with { MaxStableKhz = Stock.Points[p850].StockKhz + 600_000, AvgClockMhz = (Stock.Points[p850].StockKhz + 500_000) / 1000.0 };
        var b = Anchor(1, 900, 0) with { MaxStableKhz = Stock.Points[p900].StockKhz + 520_000, AvgClockMhz = (Stock.Points[p900].StockKhz + 420_000) / 1000.0 };
        var plus300 = new CurveOffsets([.. Enumerable.Repeat(300_000, Stock.Points.Count)]);
        var plus450 = new CurveOffsets([.. Enumerable.Repeat(450_000, Stock.Points.Count)]);

        Assert.Equal(120, CurveBuilder.MaxRaiseMhz(Stock, [a, b], plus300));
        Assert.Equal(0, CurveBuilder.MaxRaiseMhz(Stock, [a, b], plus450));
    }
}
