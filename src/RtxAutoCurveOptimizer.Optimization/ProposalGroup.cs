namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>Proposals that describe the same curve (same cap anchor, same offsets), in input order.</summary>
public sealed record ProposalGroup(IReadOnlyList<ProfileProposal> Members)
{
    /// <summary>The member that represents the group: the first one (Efficient comes first when present).</summary>
    public ProfileProposal Primary => Members[0];

    /// <summary>True if any merged proposal was validated.</summary>
    public bool Validated => Members.Any(p => p.Validated);

    /// <summary>True if the group contains the recommended (Efficient) proposal.</summary>
    public bool IsRecommended => Members.Any(p => p.Kind == ProposalKind.Efficient);

    public bool Contains(ProposalKind kind) => Members.Any(p => p.Kind == kind);

    /// <summary>Merges proposals with the same cap anchor and identical offsets into one group each.</summary>
    public static IReadOnlyList<ProposalGroup> Merge(IEnumerable<ProfileProposal> proposals)
    {
        var groups = new List<List<ProfileProposal>>();
        foreach (var p in proposals)
        {
            var match = groups.Find(g => g[0].CapAnchor.AnchorIndex == p.CapAnchor.AnchorIndex
                                         && g[0].Offsets.OffsetsKhz.SequenceEqual(p.Offsets.OffsetsKhz));
            if (match is null) groups.Add([p]);
            else match.Add(p);
        }

        return [.. groups.Select(g => new ProposalGroup(g))];
    }
}
