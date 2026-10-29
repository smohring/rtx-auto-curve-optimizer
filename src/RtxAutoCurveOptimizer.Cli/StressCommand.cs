using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Hardware;
using RtxAutoCurveOptimizer.Stress;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary><c>rtxaco stress</c>: calibrates and runs the stress test on the current curve; never changes offsets.</summary>
internal static class StressCommand
{
    public static async Task<int> RunAsync(CommandArgs args, CancellationToken ct)
    {
        var seconds = args.GetInt("seconds", 1, 86_400) ?? throw CommandArgs.Usage("--seconds is required.");
        var pattern = ParsePattern(args.Get("pattern"));
        var intensity = args.GetDouble("intensity", 0.1, 1.0) ?? 1.0;

        using var gpu = TryOpenGpu();
        using var tester = new ComputeStressTester();
        Console.WriteLine($"Adapter: {tester.AdapterName}{(tester.IsNvidia ? "" : " (not an NVIDIA GPU)")}");
        if (gpu is not null && gpu.ReadOffsets().OffsetsKhz.Any(o => o != 0))
            Console.WriteLine("Note: the GPU currently has curve offsets; golden results are computed on this curve, not on stock.");

        Console.WriteLine("Calibrating ...");
        await tester.CalibrateAsync(ct);
        Console.WriteLine($"Calibrated: {tester.Iterations} iterations per thread, {tester.ThreadCount} threads, dispatch {tester.CalibratedDispatchTime.TotalMilliseconds:F0} ms");
        Console.WriteLine($"Running {pattern} load for {seconds} s at intensity {Shared.Fixed(intensity, 2)} (Ctrl+C stops)");

        var nextPrint = TimeSpan.Zero;
        var progress = new InlineProgress<StressProgress>(p =>
        {
            if (p.Elapsed < nextPrint) return;
            nextPrint = p.Elapsed + TimeSpan.FromSeconds(1);
            Console.WriteLine($"  t={p.Elapsed.TotalSeconds,5:F1}s  throughput {p.Throughput:E3}/s  mismatches {p.MismatchCount}{TelemetryText(gpu)}");
        });
        var result = await tester.RunAsync(new StressRequest(TimeSpan.FromSeconds(seconds), pattern, intensity), progress, ct);

        Console.WriteLine($"Result: {result.Outcome}, {result.Elapsed.TotalSeconds:F1} s, mismatches {result.MismatchCount}, throughput {result.Throughput:E3}/s");
        if (!string.IsNullOrEmpty(result.Detail)) Console.WriteLine($"Detail: {result.Detail}");
        return result.Outcome switch
        {
            StressOutcome.Passed => ExitCodes.Success,
            StressOutcome.Cancelled => ExitCodes.Cancelled,
            _ => ExitCodes.Failure,
        };
    }

    private static LoadPattern ParsePattern(string? text) => (text ?? "steady").ToLowerInvariant() switch
    {
        "steady" => LoadPattern.Steady,
        "pulsed" => LoadPattern.Pulsed,
        "variable" => LoadPattern.Variable,
        _ => throw CommandArgs.Usage("--pattern must be steady, pulsed or variable."),
    };

    /// <summary>Opens the GPU for telemetry only; the stress test itself does not need it.</summary>
    private static NvidiaGpu? TryOpenGpu()
    {
        try
        {
            return NvidiaGpu.OpenFirst();
        }
        catch (GpuControlException)
        {
            return null;
        }
    }

    private static string TelemetryText(NvidiaGpu? gpu)
    {
        if (gpu is null) return "";
        try
        {
            var t = gpu.ReadTelemetry();
            return $"  |  {t.GraphicsClockMhz} MHz  {Shared.Fixed(t.VoltageUv / 1000.0, 0)} mV  {Shared.Fixed(t.PowerW, 0)} W  {t.TemperatureC} C";
        }
        catch (GpuControlException)
        {
            return "";
        }
    }
}
