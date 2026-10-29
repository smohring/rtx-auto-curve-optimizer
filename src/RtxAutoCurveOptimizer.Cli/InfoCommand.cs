using System.Text.Json;
using RtxAutoCurveOptimizer.Afterburner;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Hardware;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary>Afterburner state as shown by <c>info</c>.</summary>
internal sealed record AfterburnerState(bool Running, string? ProfileFile, IReadOnlyList<int>? EmptySlots, string? Error)
{
    /// <summary>Looks up the process and the profile file of the GPU; problems are captured, never thrown.</summary>
    public static AfterburnerState Collect(GpuIdentity identity)
    {
        var running = AfterburnerProcess.IsRunning();
        try
        {
            var store = AfterburnerProfileStore.CreateDefault();
            var file = store.FindProfileFile(identity);
            return file is null
                ? new AfterburnerState(running, null, null, null)
                : new AfterburnerState(running, file, store.Read(file).EmptySlots, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return new AfterburnerState(running, null, null, ex.Message);
        }
    }
}

/// <summary><c>rtxaco info</c>: read-only report of identity, curve, telemetry and Afterburner.</summary>
internal static class InfoCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Task<int> RunAsync(CommandArgs args, CancellationToken ct)
    {
        using var gpu = NvidiaGpu.OpenFirst();
        var stock = gpu.ReadStockCurve();
        var offsets = gpu.ReadOffsets();
        var telemetry = gpu.ReadTelemetry();
        var afterburner = AfterburnerState.Collect(gpu.Identity);

        if (args.Has("json"))
            Console.WriteLine(ToJson(gpu, stock, offsets, telemetry, afterburner));
        else
            WriteText(Console.Out, gpu, stock, offsets, telemetry, afterburner);
        return Task.FromResult(ExitCodes.Success);
    }

    private static void WriteText(
        TextWriter w, NvidiaGpu gpu, StockCurve stock, CurveOffsets offsets, Telemetry t, AfterburnerState ab)
    {
        var id = gpu.Identity;
        w.WriteLine($"GPU:        {id.Name}, driver {id.DriverVersion}");
        w.WriteLine($"PCI:        device 0x{id.DeviceId:X4}, subsystem 0x{id.SubsystemId:X8}, revision 0x{id.RevisionId:X2}, bus {id.BusNumber}");
        w.WriteLine($"Elevated:   {YesNo(gpu.CanWrite)}{(gpu.CanWrite ? "" : " (curve writes are refused)")}");
        w.WriteLine($"Curve:      {stock.Points.Count} graphics points, {Shared.Mv(stock.Points[0].VoltageUv)} - {Shared.Mv(stock.Points[^1].VoltageUv)} mV");
        w.WriteLine($"Offsets:    {Shared.OffsetRange(offsets)}");
        w.WriteLine($"Telemetry:  {t.GraphicsClockMhz} MHz core, {t.MemoryClockMhz} MHz memory, {Shared.Fixed(t.VoltageUv / 1000.0, 0)} mV, " +
                    $"{Shared.Fixed(t.PowerW, 1)} W (limit {Shared.Fixed(t.PowerLimitW, 0)} W), {t.TemperatureC} C, " +
                    $"{t.UtilizationPercent} % load, limits: {Shared.LimitText(t.LimitReasons)}");
        w.WriteLine($"Afterburner: {(ab.Running ? "running (close it before writing curves or profiles)" : "not running")}");
        w.WriteLine(ab.ProfileFile is null
            ? $"Profile:    not found{(ab.Error is null ? "" : " (" + ab.Error + ")")}"
            : $"Profile:    {ab.ProfileFile}");
        if (ab.EmptySlots is not null)
            w.WriteLine($"Empty slots: {(ab.EmptySlots.Count == 0 ? "none" : string.Join(", ", ab.EmptySlots))}");

        w.WriteLine();
        Shared.WriteTable(w, ["index", "mV", "stock MHz", "offset MHz", "effective MHz"],
            stock.Points.Select((p, i) => new[]
            {
                p.Index.ToString(), Shared.Mv(p.VoltageUv), Shared.Mhz(p.StockKhz),
                Shared.Mhz(offsets.OffsetsKhz[i]), Shared.Mhz(p.StockKhz + offsets.OffsetsKhz[i]),
            }));
    }

    private static string ToJson(NvidiaGpu gpu, StockCurve stock, CurveOffsets offsets, Telemetry t, AfterburnerState ab)
    {
        var id = gpu.Identity;
        return JsonSerializer.Serialize(new
        {
            gpu = new { name = id.Name, driverVersion = id.DriverVersion, deviceId = id.DeviceId, subsystemId = id.SubsystemId, revisionId = id.RevisionId, bus = id.BusNumber },
            elevated = gpu.CanWrite,
            telemetry = new
            {
                graphicsClockMhz = t.GraphicsClockMhz, memoryClockMhz = t.MemoryClockMhz, voltageMv = t.VoltageUv / 1000.0,
                powerW = t.PowerW, powerLimitW = t.PowerLimitW, temperatureC = t.TemperatureC,
                utilizationPercent = t.UtilizationPercent, limitReasons = Shared.LimitText(t.LimitReasons),
            },
            afterburner = new { running = ab.Running, profileFile = ab.ProfileFile, emptySlots = ab.EmptySlots, error = ab.Error },
            curve = stock.Points.Select((p, i) => new
            {
                index = p.Index, voltageMv = p.VoltageUv / 1000.0, stockMhz = p.StockKhz / 1000.0,
                offsetMhz = offsets.OffsetsKhz[i] / 1000.0, effectiveMhz = (p.StockKhz + offsets.OffsetsKhz[i]) / 1000.0,
            }),
        }, JsonOptions);
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
