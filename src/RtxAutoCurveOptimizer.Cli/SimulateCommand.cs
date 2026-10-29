using RtxAutoCurveOptimizer.Optimization;
using RtxAutoCurveOptimizer.Optimization.Simulation;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary><c>rtxaco simulate</c>: complete optimizer run on the simulated GPU with a virtual clock.</summary>
internal static class SimulateCommand
{
    public static async Task<int> RunAsync(CommandArgs args, CancellationToken ct)
    {
        var preset = Shared.ParsePreset(args.Get("preset"));
        var seed = args.GetInt("seed", int.MinValue, int.MaxValue) ?? 1;
        var powerCap = args.GetDouble("power-cap", 50, 1000);

        var options = powerCap is { } cap
            ? new SimulatedGpuOptions { PowerCapW = cap, PowerLimitW = cap }
            : new SimulatedGpuOptions();
        var clock = new SimulatedClock();
        using var gpu = new SimulatedGpu(clock, options, seed);
        using var tester = new SimulatedStressTester(gpu, clock, seed);
        var optimizer = new CurveOptimizer(gpu, tester, new InMemorySessionStore(), clock);
        var settings = OptimizerSettings.For(preset);

        Console.WriteLine($"Simulation: {gpu.Identity.Name}, preset {preset}, seed {seed}" +
                          (powerCap is { } c ? $", power cap {Shared.Num(c)} W" : "") +
                          $", anchors {string.Join("/", settings.AnchorVoltagesMv)} mV");
        var result = await optimizer.RunAsync(settings, new ConsoleReporter(args.Has("tests"), false), ct);
        ResultPrinter.Print(Console.Out, result);
        return result.Status == OptimizationStatus.Completed ? ExitCodes.Success : ExitCodes.Failure;
    }
}
