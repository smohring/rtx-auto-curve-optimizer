using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>Pure curve math: flat test curves and final curves. Indices are positions in <see cref="StockCurve.Points"/>.</summary>
public static class CurveBuilder
{
    /// <summary>Largest offset magnitude of a final curve, in kHz.</summary>
    public const int MaxOffsetKhz = 1_000_000;

    /// <summary>Offsets 0 below the point, flat at <paramref name="freqKhz"/> from the point upwards (not clamped).</summary>
    public static CurveOffsets BuildFlatTest(StockCurve stock, int pointIndex, int freqKhz)
    {
        var points = stock.Points;
        if ((uint)pointIndex >= (uint)points.Count)
            throw new ArgumentOutOfRangeException(nameof(pointIndex));
        var offsets = new int[points.Count];
        for (var j = pointIndex; j < points.Count; j++)
            offsets[j] = freqKhz - points[j].StockKhz;
        return new CurveOffsets(offsets);
    }

    /// <summary>
    /// Final curve capped at <paramref name="capAnchor"/>: interpolated deltas up to the cap, flat above,
    /// non-decreasing, offsets clamped to +-1000 MHz. Below the first anchor no point exceeds
    /// <paramref name="belowFirstAnchorLimit"/> (the user's proven curve), since that range is untested.
    /// </summary>
    public static CurveOffsets BuildFinal(
        StockCurve stock, IReadOnlyList<AnchorResult> anchors, AnchorResult capAnchor, int extraMarginMhz,
        CurveOffsets? belowFirstAnchorLimit = null)
    {
        var points = stock.Points;
        var extraKhz = extraMarginMhz * 1000;

        var used = anchors.Where(a => a.VoltageUv <= capAnchor.VoltageUv && a.PointIndex != capAnchor.PointIndex)
            .Append(capAnchor)
            .OrderBy(a => a.VoltageUv)
            .ToList();
        var anchorVoltages = used.Select(a => (double)a.VoltageUv).ToArray();
        var anchorDeltas = used.Select(a => (double)(a.RecommendedKhz - extraKhz - a.StockKhz)).ToArray();
        var capFrequency = capAnchor.RecommendedKhz - extraKhz;

        var offsets = new int[points.Count];
        var previous = int.MinValue;
        for (var j = 0; j < points.Count; j++)
        {
            int frequency;
            if (points[j].VoltageUv > capAnchor.VoltageUv)
            {
                frequency = capFrequency;
            }
            else
            {
                var delta = Interpolate(anchorVoltages, anchorDeltas, points[j].VoltageUv);
                if (belowFirstAnchorLimit is not null && points[j].VoltageUv < anchorVoltages[0])
                    delta = Math.Min(delta, belowFirstAnchorLimit.OffsetsKhz[j]);
                frequency = (int)Math.Min(Math.Round(points[j].StockKhz + delta), capFrequency);
            }

            frequency = Math.Max(frequency, previous);
            previous = frequency;
            offsets[j] = Math.Clamp(frequency - points[j].StockKhz, -MaxOffsetKhz, MaxOffsetKhz);
        }

        return new CurveOffsets(offsets);
    }

    /// <summary>
    /// How far (MHz, in 15 MHz steps) a curve capped at <paramref name="cap"/> can usefully be raised: until every anchor
    /// up to the cap sits on its own measured clock (each one stops there, see <see cref="ProposalBuilder.Shifted"/>).
    /// </summary>
    public static int MaxRaiseMhz(IReadOnlyList<AnchorResult> anchors, AnchorResult cap) =>
        Math.Max(0, anchors.Where(a => a.VoltageUv <= cap.VoltageUv).Max(a => ((ProposalBuilder.ProvenKhz(a) - a.RecommendedKhz + 999) / 1000 + 14) / 15 * 15));

    /// <summary>How far (MHz, 15 MHz steps) an arbitrary curve may be raised before it passes the measured clock at any anchor.</summary>
    public static int MaxRaiseMhz(StockCurve stock, IReadOnlyList<AnchorResult> anchors, CurveOffsets curve) =>
        anchors.Count == 0 ? 0 : Math.Max(0, anchors.Min(a =>
            (ProposalBuilder.ProvenKhz(a) - stock.Points[a.PointIndex].StockKhz - curve.OffsetsKhz[a.PointIndex]) / 1000) / 15 * 15);

    /// <summary>Position of the curve point whose voltage is nearest to <paramref name="voltageMv"/>.</summary>
    public static int NearestPointIndex(StockCurve stock, int voltageMv)
    {
        var target = voltageMv * 1000;
        var best = 0;
        for (var j = 1; j < stock.Points.Count; j++)
            if (Math.Abs(stock.Points[j].VoltageUv - target) < Math.Abs(stock.Points[best].VoltageUv - target))
                best = j;
        return best;
    }

    private static double Interpolate(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        for (var k = 1; k < xs.Length; k++)
        {
            if (x > xs[k]) continue;
            var span = xs[k] - xs[k - 1];
            return span <= 0 ? ys[k] : ys[k - 1] + (ys[k] - ys[k - 1]) * (x - xs[k - 1]) / span;
        }
        return ys[^1];
    }
}
