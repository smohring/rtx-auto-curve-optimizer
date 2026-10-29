namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>Result of one test at a frequency during the anchor search.</summary>
internal enum Verdict
{
    Pass,
    /// <summary>Instability: compute error, device loss or crash.</summary>
    Fail,
    /// <summary>Passed, but the GPU did not run at the intended clock/voltage.</summary>
    Invalid,
}

internal enum SearchAction { Test, Done, Abort }

internal readonly record struct SearchStep(SearchAction Action, int DeltaMhz);

/// <summary>Chooses the next frequency of the gallop/bisect search from the known results (delta over stock, MHz).</summary>
internal static class SearchPlanner
{
    /// <summary>
    /// Test: run at DeltaMhz. Done: DeltaMhz is the highest passing delta. Abort: nothing passes down to stock.
    /// The lowest failing delta bounds the search; passes above it are ignored.
    /// </summary>
    public static SearchStep Next(
        IReadOnlyDictionary<int, Verdict> known, int startDeltaMhz, int stepMhz, int gallopMhz, int maxDeltaMhz)
    {
        int? lowestFail = null;
        foreach (var (delta, verdict) in known)
            if (verdict != Verdict.Pass && (lowestFail is null || delta < lowestFail)) lowestFail = delta;

        int? highestPass = null;
        foreach (var (delta, verdict) in known)
            if (verdict == Verdict.Pass && (lowestFail is null || delta < lowestFail)
                && (highestPass is null || delta > highestPass))
                highestPass = delta;

        if (highestPass is null && lowestFail is null)
            return new SearchStep(SearchAction.Test, Math.Clamp(startDeltaMhz, 0, maxDeltaMhz));

        if (lowestFail is null)
        {
            var pass = highestPass!.Value;
            return pass >= maxDeltaMhz
                ? new SearchStep(SearchAction.Done, pass)
                : new SearchStep(SearchAction.Test, Math.Min(pass + gallopMhz, maxDeltaMhz));
        }

        if (highestPass is null)
        {
            var fail = lowestFail.Value;
            return fail <= 0
                ? new SearchStep(SearchAction.Abort, fail)
                : new SearchStep(SearchAction.Test, Math.Max(fail - gallopMhz, 0));
        }

        var gap = lowestFail.Value - highestPass.Value;
        if (gap <= stepMhz) return new SearchStep(SearchAction.Done, highestPass.Value);
        var half = Math.Max(stepMhz, gap / 2 / stepMhz * stepMhz);
        return new SearchStep(SearchAction.Test, highestPass.Value + half);
    }

    /// <summary>Rounds up to a multiple of <paramref name="stepMhz"/>.</summary>
    public static int RoundUpToGrid(double deltaMhz, int stepMhz) =>
        (int)(Math.Ceiling(deltaMhz / stepMhz) * stepMhz);
}
