using RtxAutoCurveOptimizer.Afterburner;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Hardware;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary>
/// <c>rtxaco write-test</c>: proves the curve write path with safe changes only (original offsets, then a lower clock,
/// then the original again). Needs administrator rights; applies no GPU load.
/// </summary>
internal static class WriteTestCommand
{
    private const int LowerKhz = 15_000;
    private const int ToleranceKhz = 7_500;

    public static Task<int> RunAsync(CommandArgs args, CancellationToken ct)
    {
        Shared.RequireAdministrator("write-test");
        using var gpu = NvidiaGpu.OpenFirst();
        var stock = gpu.ReadStockCurve();
        var original = gpu.ReadOffsets();
        var raw = TryOpenRaw();
        var baseline = raw?.Capture();

        Console.WriteLine($"GPU: {gpu.Identity.Name}, driver {gpu.Identity.DriverVersion}");
        Console.WriteLine($"Original offsets: {Shared.OffsetRange(original)} on {original.OffsetsKhz.Count} graphics points");
        if (baseline is not null)
        {
            Console.WriteLine("Non-graphics state (compared byte for byte after every step):");
            foreach (var line in baseline.Describe()) Console.WriteLine($"  {line}");
        }

        if (AfterburnerProcess.IsRunning())
            Console.WriteLine("warning: MSI Afterburner is running and may re-apply its own profile during the test.");

        var context = new Context(gpu, stock, original, raw, baseline);
        var results = new List<bool>();
        var aborted = false;
        try
        {
            gpu.ApplyOffsets(original);
            results.Add(Check(context, "Step A", "apply the original offsets unchanged", original, 0));
            ct.ThrowIfCancellationRequested();

            var lowered = new CurveOffsets(original.OffsetsKhz.Select(o => VfLayout.ClampOffsetKhz(o - LowerKhz)).ToList());
            gpu.ApplyOffsets(lowered);
            results.Add(Check(context, "Step B", $"apply original - {LowerKhz} kHz on every point (lower clock)", lowered, ToleranceKhz));
        }
        catch (OperationCanceledException)
        {
            aborted = true;
            Console.WriteLine("Cancelled; restoring the original offsets.");
        }
        catch (Exception ex)
        {
            results.Add(false);
            Console.WriteLine($"[FAIL] test aborted: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            results.Add(RestoreOriginal(context));
        }

        var passed = results.All(r => r);
        Console.WriteLine(passed && !aborted
            ? "RESULT: PASS - the curve write path works and the GPU is back at its original offsets."
            : "RESULT: FAIL");
        return Task.FromResult(aborted ? ExitCodes.Cancelled : passed ? ExitCodes.Success : ExitCodes.Failure);
    }

    /// <summary>Step C: writes the original offsets (one retry) and requires an exact read-back.</summary>
    private static bool RestoreOriginal(Context c)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                c.Gpu.ApplyOffsets(c.Original);
                break;
            }
            catch (Exception ex) when (attempt == 1)
            {
                Console.WriteLine($"warning: restoring the original offsets failed ({ex.Message}); retrying.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] Step C - restore the original offsets: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"       The GPU may still carry the step B offsets. Run 'rtxaco reset' (sets all offsets to zero) from an elevated shell; original were {Shared.OffsetRange(c.Original)}.");
                return false;
            }
        }

        try
        {
            return Check(c, "Step C", "restore the original offsets", c.Original, 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Step C - read-back after restore: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads the offsets back and prints PASS/FAIL for the offsets (within tolerance) and for the non-graphics state.</summary>
    private static bool Check(Context c, string step, string title, CurveOffsets expected, int toleranceKhz)
    {
        var actual = c.Gpu.ReadOffsets();
        var offsetsOk = actual.OffsetsKhz.Count == expected.OffsetsKhz.Count;
        var lines = new List<string>();
        if (!offsetsOk)
        {
            lines.Add($"point count changed: expected {expected.OffsetsKhz.Count}, read {actual.OffsetsKhz.Count}");
        }
        else
        {
            var deviations = expected.OffsetsKhz.Zip(actual.OffsetsKhz, (e, a) => Math.Abs(a - e)).ToList();
            var worst = deviations.Max();
            var worstAt = c.Stock.Points[deviations.IndexOf(worst)];
            offsetsOk = worst <= toleranceKhz;
            lines.Add($"offsets: max deviation from requested {worst} kHz (point {worstAt.Index}, {Shared.Mv(worstAt.VoltageUv)} mV), " +
                      $"tolerance {toleranceKhz} kHz, {deviations.Count(d => d != 0)} of {deviations.Count} points deviate");
            if (worst != 0)
                lines.Add("deviation per point: " + string.Join(", ", deviations.GroupBy(d => d).OrderBy(g => g.Key).Select(g => $"{g.Key} kHz x{g.Count()}")));
            var shift = actual.OffsetsKhz.Zip(c.Original.OffsetsKhz, (a, o) => a - o).ToList();
            lines.Add($"change versus original: {shift.Min()} .. {shift.Max()} kHz");
        }

        var stateOk = true;
        if (c.Raw is null || c.Baseline is null)
        {
            lines.Add("non-graphics state: not compared (raw access unavailable)");
        }
        else
        {
            var diffs = RawVfSnapshot.DiffNonGraphics(c.Baseline, c.Raw.Capture());
            stateOk = diffs.Count == 0;
            lines.Add(stateOk ? "non-graphics state: unchanged" : "non-graphics state: CHANGED");
            lines.AddRange(diffs.Select(d => "  " + d));
        }

        var ok = offsetsOk && stateOk;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {step} - {title}");
        foreach (var line in lines) Console.WriteLine($"       {line}");
        return ok;
    }

    private static RawVfReader? TryOpenRaw()
    {
        try
        {
            return RawVfReader.Open();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or GpuControlException)
        {
            Console.WriteLine($"note: raw Status/Control access unavailable ({ex.Message}); the non-graphics state is NOT compared.");
            return null;
        }
    }

    private sealed record Context(NvidiaGpu Gpu, StockCurve Stock, CurveOffsets Original, RawVfReader? Raw, RawVfSnapshot? Baseline);
}
