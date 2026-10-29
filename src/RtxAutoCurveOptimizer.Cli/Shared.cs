using System.Globalization;
using System.Text;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary>Progress sink that runs the callback on the reporting thread (unlike <see cref="Progress{T}"/>).</summary>
internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

/// <summary>Helpers shared by several commands: elevation guard, session loading, proposal selection, formatting.</summary>
internal static class Shared
{
    /// <summary>Refuses before anything is touched when the process is not elevated.</summary>
    public static void RequireAdministrator(string command)
    {
        if (!Environment.IsPrivilegedProcess)
            throw new CliException(
                $"'{command}' changes the GPU curve and needs an elevated (administrator) shell. Nothing was changed.",
                ExitCodes.NotElevated);
    }

    /// <summary>Loads a session from a .json path, or from an id in the default session folder; also returns the store holding it.</summary>
    public static SessionState LoadSession(string pathOrId, out JsonSessionStore store)
    {
        var full = Path.GetFullPath(pathOrId);
        if (!File.Exists(full))
        {
            var byId = Path.Combine(JsonSessionStore.DefaultDirectory, pathOrId + ".json");
            if (!File.Exists(byId)) throw new CliException($"Session file not found: {full}");
            full = byId;
        }

        if (!string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase))
            throw new CliException($"A session file must be a .json file: {full}");

        SessionState? session;
        store = new JsonSessionStore(Path.GetDirectoryName(full));
        try
        {
            session = store.Load(Path.GetFileNameWithoutExtension(full));
        }
        catch (ArgumentException)
        {
            session = null;
        }

        return session ?? throw new CliException($"Cannot read session file (invalid name or content): {full}");
    }

    /// <summary>Parses efficient, maxefficiency or performance (case-insensitive).</summary>
    public static ProposalKind ParseProposal(string text) => text.ToLowerInvariant() switch
    {
        "efficient" => ProposalKind.Efficient,
        "maxefficiency" => ProposalKind.MaxEfficiency,
        "performance" => ProposalKind.Performance,
        _ => throw CommandArgs.Usage("--proposal must be efficient, maxefficiency or performance."),
    };

    public static ProfileProposal FindProposal(SessionState session, ProposalKind kind) =>
        session.Proposals.FirstOrDefault(p => p.Kind == kind)
        ?? throw new CliException($"Session {session.Id} has no '{kind}' proposal (status {session.Status}).");

    public static OptimizationPreset ParsePreset(string? text) => (text ?? "standard").ToLowerInvariant() switch
    {
        "quick" => OptimizationPreset.Quick,
        "standard" => OptimizationPreset.Standard,
        "thorough" => OptimizationPreset.Thorough,
        _ => throw CommandArgs.Usage("--preset must be quick, standard or thorough."),
    };

    /// <summary>Frequency or voltage in kHz/uV as a decimal with at most one fraction digit.</summary>
    public static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Names of the known limit reasons; bits the enum does not define are shown as hex.</summary>
    public static string LimitText(ClockLimitReasons reasons)
    {
        var known = Enum.GetValues<ClockLimitReasons>().Aggregate(ClockLimitReasons.None, (all, r) => all | r);
        var unknown = (ulong)reasons & ~(ulong)known;
        var named = reasons & known;
        var parts = new List<string>();
        if (named != ClockLimitReasons.None) parts.Add(named.ToString());
        if (unknown != 0) parts.Add($"unknown 0x{unknown:X}");
        return parts.Count == 0 ? "None" : string.Join(", ", parts);
    }

    public static string Fixed(double value, int digits) => value.ToString("F" + digits, CultureInfo.InvariantCulture);

    public static string Mhz(int khz) => Num(khz / 1000.0);

    public static string Mv(int uv) => Num(uv / 1000.0);

    /// <summary>Compact duration such as 3m20s or 1h05m.</summary>
    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:00}m"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m{span.Seconds:00}s"
        : $"{span.TotalSeconds:F0}s";

    /// <summary>Writes a table with right-aligned columns separated by two spaces.</summary>
    public static void WriteTable(TextWriter writer, string[] headers, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) =>
            string.Join("  ", cells.Select((c, i) => c.PadLeft(widths[i]))).TrimEnd();
        writer.WriteLine(Line(headers));
        foreach (var row in all) writer.WriteLine(Line(row));
    }

    /// <summary>Largest absolute difference in MHz between two offset sets.</summary>
    public static double MaxDifferenceMhz(CurveOffsets a, CurveOffsets b) =>
        a.OffsetsKhz.Zip(b.OffsetsKhz, (x, y) => Math.Abs(x - y) / 1000.0).DefaultIfEmpty(0).Max();

    public static string OffsetRange(CurveOffsets offsets) =>
        offsets.OffsetsKhz.Count == 0 ? "none"
        : offsets.OffsetsKhz.Min() == offsets.OffsetsKhz.Max()
            ? $"{Mhz(offsets.OffsetsKhz[0])} MHz on all points"
            : $"{Mhz(offsets.OffsetsKhz.Min())} .. {Mhz(offsets.OffsetsKhz.Max())} MHz";

    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
}
