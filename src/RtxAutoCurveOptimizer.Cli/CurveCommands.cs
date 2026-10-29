using RtxAutoCurveOptimizer.Afterburner;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Hardware;
using RtxAutoCurveOptimizer.Optimization;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary>Commands that write curve offsets to the GPU; each checks elevation before anything else.</summary>
internal static class CurveCommands
{
    public static Task<int> ApplyAsync(CommandArgs args, CancellationToken ct)
    {
        Shared.RequireAdministrator("apply");
        var session = Shared.LoadSession(args.Positionals[0], out _);
        var proposal = Shared.FindProposal(session, Shared.ParseProposal(args.Get("proposal") ?? "efficient"));

        using var gpu = NvidiaGpu.OpenFirst();
        RequireSameGpu(session, gpu);
        if (!proposal.Validated)
            Console.WriteLine("note: this proposal was not validated under variable load.");
        Console.WriteLine($"Applying {proposal.Kind} of session {session.Id}: cap {Shared.Mv(proposal.CapAnchor.VoltageUv)} mV, offsets {Shared.OffsetRange(proposal.Offsets)}");
        WriteAndVerify(gpu, proposal.Offsets);
        WarnIfAfterburnerRuns();
        return Task.FromResult(ExitCodes.Success);
    }

    public static Task<int> ResetAsync(CommandArgs args, CancellationToken ct)
    {
        Shared.RequireAdministrator("reset");
        using var gpu = NvidiaGpu.OpenFirst();
        var current = gpu.ReadOffsets();
        Console.WriteLine($"Current offsets: {Shared.OffsetRange(current)}");
        WriteAndVerify(gpu, CurveOffsets.Zero(current.OffsetsKhz.Count));
        WarnIfAfterburnerRuns();
        return Task.FromResult(ExitCodes.Success);
    }

    public static Task<int> RestoreAsync(CommandArgs args, CancellationToken ct)
    {
        Shared.RequireAdministrator("restore");
        var session = Shared.LoadSession(args.Positionals[0], out var store);

        using var gpu = NvidiaGpu.OpenFirst();
        RequireSameGpu(session, gpu);
        Console.WriteLine($"Restoring original offsets of session {session.Id}: {Shared.OffsetRange(session.OriginalOffsets)}");
        if (session.Status == SessionStatus.InProgress)
        {
            var recovered = new SessionRecovery(store).Recover(session, gpu);
            Console.WriteLine($"Session was unfinished; marked Interrupted ({recovered.Restore}{(recovered.CrashedTestRecorded ? ", in-flight test recorded as crashed" : "")}).");
            if (recovered.Restore != RecoveryRestore.Restored)
                throw new CliException($"Restore failed: {recovered.Detail ?? recovered.Restore.ToString()}");
            Verify(gpu, session.OriginalOffsets);
        }
        else
        {
            WriteAndVerify(gpu, session.OriginalOffsets);
        }

        WarnIfAfterburnerRuns();
        return Task.FromResult(ExitCodes.Success);
    }

    private static void RequireSameGpu(SessionState session, NvidiaGpu gpu)
    {
        if (!SessionRecovery.Matches(session, gpu) || session.Gpu.Name != gpu.Identity.Name)
            throw new CliException($"Session {session.Id} was recorded on a different GPU ({session.Gpu.Name}); refusing to write its offsets.");
    }

    private static void WriteAndVerify(NvidiaGpu gpu, CurveOffsets offsets)
    {
        gpu.ApplyOffsets(offsets);
        Verify(gpu, offsets);
    }

    /// <summary>Reads the offsets back; the driver quantizes frequencies, so small deviations are normal.</summary>
    private static void Verify(NvidiaGpu gpu, CurveOffsets requested)
    {
        var actual = gpu.ReadOffsets();
        Console.WriteLine($"Offsets now: {Shared.OffsetRange(actual)} (largest deviation from requested {Shared.Fixed(Shared.MaxDifferenceMhz(requested, actual), 1)} MHz)");
    }

    private static void WarnIfAfterburnerRuns()
    {
        if (AfterburnerProcess.IsRunning())
            Console.WriteLine("warning: MSI Afterburner is running and may re-apply its own profile over this change.");
    }
}
