namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>Rebuilds anchor recommendations and proposals of a stored session with the current rules.</summary>
public static class SessionRecompute
{
    /// <summary>
    /// Recommendations from the measured clocks, proposals with the original curve as limit below the first anchor;
    /// none validated. <paramref name="marginMhz"/> replaces the session's safety margin.
    /// </summary>
    public static SessionState Recompute(SessionState session, int? marginMhz = null)
    {
        var margin = marginMhz ?? session.Settings.SafetyMarginMhz;
        var anchors = session.Anchors
            .Select(a => a with { RecommendedKhz = ProposalBuilder.RecommendedKhz(a, margin) })
            .ToList();
        var proposals = ProposalBuilder.Build(session.Stock, anchors, session.OriginalOffsets);
        return session with { Settings = session.Settings with { SafetyMarginMhz = margin }, Anchors = anchors, Proposals = [.. proposals] };
    }
}
