using RtxAutoCurveOptimizer.Optimization;
using RtxAutoCurveOptimizer.Afterburner;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary>Commands that only write files: curve export and the Afterburner profile slot. They never touch the GPU.</summary>
internal static class FileCommands
{
    public static Task<int> ExportAsync(CommandArgs args, CancellationToken ct)
    {
        var session = Shared.LoadSession(args.Positionals[0], out _);
        var proposal = Shared.FindProposal(session, Shared.ParseProposal(Required(args, "proposal")));
        var text = (args.Get("format") ?? "csv").ToLowerInvariant() switch
        {
            "csv" => CurveExport.ToCsv(session.Stock, proposal.Offsets),
            "json" => CurveExport.ToJson(session.Stock, proposal.Offsets),
            _ => throw CommandArgs.Usage("--format must be csv or json."),
        };

        if (args.Get("out") is { } path)
        {
            File.WriteAllText(path, text, Shared.Utf8NoBom);
            Console.WriteLine($"Wrote {proposal.Kind} of session {session.Id} to {Path.GetFullPath(path)}");
        }
        else
        {
            Console.Out.Write(text);
        }
        return Task.FromResult(ExitCodes.Success);
    }

    /// <summary>Recomputes a stored session (measured clocks, proven curve below the first anchor) into a new session.</summary>
    public static Task<int> RecomputeAsync(CommandArgs args, CancellationToken ct)
    {
        var session = Shared.LoadSession(args.Positionals[0], out var store);
        var recomputed = SessionRecompute.Recompute(session, args.GetInt("margin", 0, 300)) with { Id = session.Id + "-recomputed" };
        store.Save(recomputed);
        ResultPrinter.Print(Console.Out, recomputed.ToResult());
        Console.WriteLine($"Saved as session {recomputed.Id} next to {Path.GetFullPath(args.Positionals[0])}");
        return Task.FromResult(ExitCodes.Success);
    }

    public static Task<int> AfterburnerAsync(CommandArgs args, CancellationToken ct)
    {
        var session = Shared.LoadSession(args.Positionals[0], out _);
        var proposal = Shared.FindProposal(session, Shared.ParseProposal(Required(args, "proposal")));
        var slot = args.GetInt("slot", 1, AfterburnerProfile.SlotCount) ?? throw CommandArgs.Usage("--slot is required (1 to 5).");

        if (AfterburnerProcess.IsRunning() && !args.Has("force"))
            throw new CliException("MSIAfterburner.exe is running and rewrites its profiles on exit. Close it, or use --force to write anyway.");

        var store = args.Get("profiles") is { } dir ? new AfterburnerProfileStore(dir) : AfterburnerProfileStore.CreateDefault();
        var file = store.FindProfileFile(session.Gpu)
            ?? throw new CliException($"No Afterburner profile file for {session.Gpu.Name} found in {store.ProfilesDirectory}.");
        var profile = store.Read(file);
        if (!profile.IsSlotEmpty(slot))
            Console.WriteLine($"note: slot {slot} already holds a curve and will be overwritten (a backup is made first).");

        var existing = profile.GetCurve("Startup") ?? profile.GetCurve("Defaults");
        var length = existing is not null && existing.LengthBytes >= 12 + session.Stock.Points.Count * 12
            ? existing.LengthBytes
            : AfterburnerVfCurve.DefaultLengthBytes;
        var curve = AfterburnerVfCurve.Create(session.Stock, proposal.Offsets, length);
        var capOffsetKhz = proposal.Offsets.OffsetsKhz[proposal.CapAnchor.PointIndex];

        try
        {
            var backup = store.WriteProfile(file, slot, curve, capOffsetKhz);
            Console.WriteLine($"Wrote {proposal.Kind} of session {session.Id} to slot {slot} of {file}");
            Console.WriteLine($"Backup: {backup}");
        }
        catch (UnauthorizedAccessException)
        {
            throw new CliException($"Access to {file} was denied; run an elevated shell (the profile folder is under Program Files).", ExitCodes.NotElevated);
        }
        return Task.FromResult(ExitCodes.Success);
    }

    private static string Required(CommandArgs args, string name) =>
        args.Get(name) ?? throw CommandArgs.Usage($"--{name} is required.");
}
