using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>Picks the cap anchor of each proposal kind and builds the curves.</summary>
public static class ProposalBuilder
{
    /// <summary>
    /// Builds Efficient, MaxEfficiency and Performance proposals. Limited anchors are only considered
    /// when no unrestricted anchor exists, because their power and throughput were measured at reduced load.
    /// </summary>
    public static IReadOnlyList<ProfileProposal> Build(
        StockCurve stock, IReadOnlyList<AnchorResult> anchors, CurveOffsets? belowFirstAnchorLimit = null)
    {
        if (anchors.Count == 0) return [];
        var ordered = anchors.OrderBy(a => a.VoltageUv).ToList();
        var candidates = ordered.Where(a => !a.Limited).ToList();
        if (candidates.Count == 0) candidates = ordered;

        var efficient = candidates.FirstOrDefault(a => PredictedRelative(a, 0) >= 1.0)
                        ?? MaxBy(candidates, a => PredictedRelative(a, 0));
        var maxEfficiency = MaxBy(candidates, a => a.PerfPerWatt * PredictedFactor(a, 0));
        var performance = MaxBy(candidates, a => PredictedRelative(a, 0));

        return
        [
            Create(stock, ordered, ProposalKind.Efficient, efficient, 0, false, belowFirstAnchorLimit),
            Create(stock, ordered, ProposalKind.MaxEfficiency, maxEfficiency, 0, false, belowFirstAnchorLimit),
            Create(stock, ordered, ProposalKind.Performance, performance, 0, false, belowFirstAnchorLimit),
        ];
    }

    /// <summary>Builds a proposal for an arbitrary cap anchor with the given extra margin.</summary>
    public static ProfileProposal Create(
        StockCurve stock, IReadOnlyList<AnchorResult> anchors, ProposalKind kind, AnchorResult cap,
        int extraMarginMhz, bool validated = false, CurveOffsets? belowFirstAnchorLimit = null) =>
        new(kind, cap, CurveBuilder.BuildFinal(stock, anchors, cap, extraMarginMhz, belowFirstAnchorLimit), extraMarginMhz,
            cap.AvgPowerW, PredictedRelative(cap, extraMarginMhz), validated);

    /// <summary>
    /// The proposal shifted by <paramref name="shiftMhz"/>: lowering moves every anchor down; raising lifts every anchor
    /// up to its own <see cref="ProvenKhz"/> and no further, so the weakest anchor does not hold back the others.
    /// </summary>
    public static ProfileProposal Shifted(
        StockCurve stock, IReadOnlyList<AnchorResult> anchors, ProfileProposal proposal, int shiftMhz, CurveOffsets? belowFirstAnchorLimit)
    {
        var extraKhz = proposal.ExtraMarginMhz * 1000;
        var shifted = anchors.Select(a => a with
        {
            RecommendedKhz = Math.Min(a.RecommendedKhz + shiftMhz * 1000, Math.Max(ProvenKhz(a) + extraKhz, a.RecommendedKhz)),
        }).ToList();
        var cap = shifted.Single(a => a.PointIndex == proposal.CapAnchor.PointIndex);
        return Create(stock, shifted, proposal.Kind, cap, proposal.ExtraMarginMhz, shiftMhz == 0 && proposal.Validated, belowFirstAnchorLimit);
    }

    /// <summary>
    /// Recommended curve value of an anchor: the clock measured under load (never the higher curve value the test
    /// set, since a cold GPU runs the full curve value) minus the safety margin.
    /// </summary>
    public static int RecommendedKhz(int maxStableKhz, double avgClockMhz, int marginMhz) =>
        (avgClockMhz > 0 ? Math.Min(maxStableKhz, (int)Math.Round(avgClockMhz * 1000)) : maxStableKhz) - marginMhz * 1000;

    /// <summary>
    /// The shifted proposal with its top clock set to <paramref name="limitMhz"/>: below the proposal's own peak the curve is
    /// cut off there; above it the cap moves to the lowest higher anchor whose curve reaches the limit.
    /// </summary>
    public static ProfileProposal Limited(
        StockCurve stock, IReadOnlyList<AnchorResult> anchors, ProfileProposal proposal, int shiftMhz, int limitMhz,
        CurveOffsets? belowFirstAnchorLimit)
    {
        var candidates = CapCandidates(stock, anchors, proposal, shiftMhz, belowFirstAnchorLimit);
        var p = candidates.FirstOrDefault(c => PeakMhz(stock, c) >= limitMhz) ?? candidates[^1];
        if (PeakMhz(stock, p) <= limitMhz) return p;
        var offsets = p.Offsets.OffsetsKhz.Select((o, j) => Math.Min(o, limitMhz * 1000 - stock.Points[j].StockKhz));
        return p with { Offsets = new CurveOffsets([.. offsets]), Validated = false };
    }

    /// <summary>
    /// The common manual method: one offset for the whole curve, flat from where it reaches the stock boost clock. The
    /// offset is the smallest recommended one among anchors below that clock (the others only carry the flat part).
    /// Like the manual method it also applies below the first anchor, which is untested. <paramref name="shiftMhz"/>
    /// changes the offset, <paramref name="capMhz"/> moves the flat part (default: the stock boost clock).
    /// </summary>
    public static ProfileProposal Uniform(
        StockCurve stock, IReadOnlyList<AnchorResult> anchors, double stockBoostMhz, int shiftMhz = 0, int? capMhz = null)
    {
        var offsetKhz = UniformOffsetKhz(anchors, stockBoostMhz) + shiftMhz * 1000;
        var capKhz = (capMhz ?? (int)Math.Round(stockBoostMhz)) * 1000;
        var offsets = new int[stock.Points.Count];
        var previous = int.MinValue;
        for (var j = 0; j < offsets.Length; j++)
        {
            var p = stock.Points[j];
            previous = Math.Max(previous, Math.Min(p.StockKhz + offsetKhz, capKhz));
            offsets[j] = Math.Clamp(previous - p.StockKhz, -CurveBuilder.MaxOffsetKhz, CurveBuilder.MaxOffsetKhz);
        }

        var curve = new CurveOffsets(offsets);
        var e = Estimate(stock, anchors, curve);
        var cap = anchors.OrderBy(a => a.VoltageUv).FirstOrDefault(a => a.VoltageUv >= e.CapVoltageUv) ?? anchors.MaxBy(a => a.VoltageUv)!;
        return new ProfileProposal(ProposalKind.Uniform, cap, curve, 0, e.PowerW, e.RelativePerformance, false);
    }

    /// <summary>
    /// Highest cap of <see cref="Uniform"/> with the same offset: the flat part may not pass any anchor's proven clock,
    /// and the cap may not sit above the highest anchor (untested voltages).
    /// </summary>
    public static int UniformMaxCapMhz(StockCurve stock, IReadOnlyList<AnchorResult> anchors, double stockBoostMhz, int shiftMhz)
    {
        var offsetKhz = UniformOffsetKhz(anchors, stockBoostMhz) + shiftMhz * 1000;
        var top = anchors.MaxBy(a => a.VoltageUv)!;
        return anchors.Where(a => a.StockKhz + offsetKhz > ProvenKhz(a)).Select(ProvenKhz)
            .Append(Math.Min(top.StockKhz + offsetKhz, ProvenKhz(top))).Min() / 1000;
    }

    /// <summary>The offset of <see cref="Uniform"/>: the smallest recommended one among anchors below the stock boost clock.</summary>
    private static int UniformOffsetKhz(IReadOnlyList<AnchorResult> anchors, double stockBoostMhz)
    {
        var boostKhz = (int)Math.Round(stockBoostMhz) * 1000;
        return anchors.Where(a => a.RecommendedKhz < boostKhz).DefaultIfEmpty(anchors.MinBy(a => a.VoltageUv)!)
            .Min(a => a.RecommendedKhz - a.StockKhz);
    }

    /// <summary>Highest top clock <see cref="Limited"/> can reach for the proposal: the best curve capped at it or any higher anchor.</summary>
    public static int MaxLimitMhz(
        StockCurve stock, IReadOnlyList<AnchorResult> anchors, ProfileProposal proposal, int shiftMhz, CurveOffsets? belowFirstAnchorLimit) =>
        CapCandidates(stock, anchors, proposal, shiftMhz, belowFirstAnchorLimit).Max(c => PeakMhz(stock, c));

    /// <summary>Top clock of a curve in MHz.</summary>
    public static int PeakMhz(StockCurve stock, ProfileProposal p) =>
        stock.Points.Select((pt, j) => pt.StockKhz + p.Offsets.OffsetsKhz[j]).Max() / 1000;

    /// <summary>The shifted proposal, then the same proposal capped at each higher anchor, lowest voltage first.</summary>
    private static List<ProfileProposal> CapCandidates(
        StockCurve stock, IReadOnlyList<AnchorResult> anchors, ProfileProposal proposal, int shiftMhz, CurveOffsets? belowFirstAnchorLimit) =>
    [
        Shifted(stock, anchors, proposal, shiftMhz, belowFirstAnchorLimit),
        .. anchors.Where(a => a.VoltageUv > proposal.CapAnchor.VoltageUv).OrderBy(a => a.VoltageUv).Select(a =>
            Shifted(stock, anchors, Create(stock, anchors, proposal.Kind, a, proposal.ExtraMarginMhz, false, belowFirstAnchorLimit),
                shiftMhz, belowFirstAnchorLimit)),
    ];

    /// <summary>
    /// Highest clock an anchor actually ran stable: the hot measured clock or a passed cold burst, whichever is higher.
    /// The ceiling for raising a curve there.
    /// </summary>
    public static int ProvenKhz(AnchorResult a) => Math.Max(RecommendedKhz(a.MaxStableKhz, a.AvgClockMhz, 0), a.ColdProvenKhz);

    /// <summary>Recommended curve value of an anchor: its proven clock minus the safety margin.</summary>
    public static int RecommendedKhz(AnchorResult a, int marginMhz) => ProvenKhz(a) - marginMhz * 1000;

    /// <summary>
    /// Performance relative to stock expected with the final curve: under load the clock drops as far below the
    /// recommended value as it dropped below the test curve.
    /// </summary>
    public static double PredictedRelative(AnchorResult a, int extraMarginMhz) =>
        a.ThroughputRelative * PredictedFactor(a, extraMarginMhz);

    /// <summary>Loaded clock with the final curve divided by the clock measured in the confirm test.</summary>
    private static double PredictedFactor(AnchorResult a, int extraMarginMhz) => ClockFactor(a, a.RecommendedKhz / 1000.0 - extraMarginMhz);

    /// <summary>Loaded clock of a curve topping out at <paramref name="topMhz"/> at this anchor, divided by the measured clock.</summary>
    private static double ClockFactor(AnchorResult a, double topMhz)
    {
        if (a.AvgClockMhz <= 0) return 1.0;
        var dropMhz = Math.Max(0, a.MaxStableKhz / 1000.0 - a.AvgClockMhz);
        return Math.Min(1.0, (topMhz - dropMhz) / a.AvgClockMhz);
    }

    /// <summary>
    /// Operating point under full load of an arbitrary curve: its top clock at the lowest voltage that reaches it, with
    /// power and performance interpolated between the neighbouring anchors.
    /// </summary>
    public static (int CapVoltageUv, int TopKhz, double PowerW, double RelativePerformance) Estimate(
        StockCurve stock, IReadOnlyList<AnchorResult> anchors, CurveOffsets offsets)
    {
        var top = stock.Points.Select((p, j) => p.StockKhz + offsets.OffsetsKhz[j]).Max();
        var voltage = stock.Points.Where((p, j) => p.StockKhz + offsets.OffsetsKhz[j] == top).Min(p => p.VoltageUv);
        var ordered = anchors.OrderBy(a => a.VoltageUv).ToList();
        var lo = ordered.LastOrDefault(a => a.VoltageUv <= voltage) ?? ordered[0];
        var hi = ordered.FirstOrDefault(a => a.VoltageUv >= voltage) ?? ordered[^1];
        var t = hi.VoltageUv == lo.VoltageUv ? 0 : (double)(voltage - lo.VoltageUv) / (hi.VoltageUv - lo.VoltageUv);
        double Relative(AnchorResult a) => a.ThroughputRelative * ClockFactor(a, top / 1000.0);
        return (voltage, top, lo.AvgPowerW + (hi.AvgPowerW - lo.AvgPowerW) * t, Relative(lo) + (Relative(hi) - Relative(lo)) * t);
    }

    /// <summary>Highest key; values within 0.2 % count as a tie and keep the lower-voltage anchor.</summary>
    private static AnchorResult MaxBy(List<AnchorResult> ordered, Func<AnchorResult, double> key)
    {
        var best = ordered[0];
        foreach (var a in ordered.Skip(1))
            if (key(a) > key(best) * 1.002) best = a;
        return best;
    }
}
