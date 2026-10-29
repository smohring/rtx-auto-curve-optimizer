using RtxAutoCurveOptimizer.Afterburner;
using RtxAutoCurveOptimizer.Hardware;
using RtxAutoCurveOptimizer.Optimization;
using RtxAutoCurveOptimizer.Stress;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary><c>rtxaco run</c>: real optimization on the GPU; Ctrl+C cancels and restores the original offsets.</summary>
internal static class RunCommand
{
    public static async Task<int> RunAsync(CommandArgs args, CancellationToken ct)
    {
        Shared.RequireAdministrator("run");
        var settings = OptimizerSettings.For(Shared.ParsePreset(args.Get("preset")));
        if (args.GetInt("margin", 0, 300) is { } margin) settings = settings with { SafetyMarginMhz = margin };

        if (AfterburnerProcess.IsRunning())
        {
            if (!args.Has("force"))
                throw new CliException("MSIAfterburner.exe is running and re-applies its own profile, which would corrupt the test curves. Close it, or use --force to run anyway.");
            Console.WriteLine("warning: MSI Afterburner is running (--force); results may be unreliable.");
        }

        using var gpu = NvidiaGpu.OpenFirst();
        var store = new JsonSessionStore();
        RecoverUnfinished(store, gpu);

        using var tester = new ComputeStressTester();
        var optimizer = new CurveOptimizer(gpu, tester, store);
        Console.WriteLine($"{gpu.Identity.Name}, stress adapter {tester.AdapterName}");
        Console.WriteLine($"Anchors {string.Join("/", settings.AnchorVoltagesMv)} mV, safety margin {settings.SafetyMarginMhz} MHz, estimated {Shared.Duration(settings.EstimateDuration())}. Ctrl+C cancels and restores the original offsets.");

        var result = await optimizer.RunAsync(settings, new ConsoleReporter(true, true), ct);
        ResultPrinter.Print(Console.Out, result);
        Console.WriteLine($"Session file: {Path.Combine(JsonSessionStore.DefaultDirectory, result.SessionId + ".json")}");
        if (result.Recommended is not null)
            Console.WriteLine($"Apply with: rtxaco apply {result.SessionId} --proposal efficient");

        return result.Status switch
        {
            OptimizationStatus.Completed => ExitCodes.Success,
            OptimizationStatus.Cancelled => ExitCodes.Cancelled,
            _ => ExitCodes.Failure,
        };
    }

    /// <summary>A session still marked in progress means an earlier run crashed: restore its offsets before starting.</summary>
    private static void RecoverUnfinished(ISessionStore store, NvidiaGpu gpu)
    {
        var unfinished = new SessionRecovery(store).FindUnfinished();
        if (unfinished is null) return;
        var recovered = new SessionRecovery(store).Recover(unfinished, gpu);
        Console.WriteLine($"Found unfinished session {unfinished.Id} (crash or hang): {recovered.Restore}" +
                          (recovered.CrashedTestRecorded ? ", in-flight test recorded as crashed" : "") +
                          (recovered.Detail is null ? "" : $" ({recovered.Detail})"));
    }
}
