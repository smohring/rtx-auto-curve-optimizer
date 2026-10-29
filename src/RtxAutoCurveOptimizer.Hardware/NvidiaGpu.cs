using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Hardware;

/// <summary>NVIDIA GPU control through private NVAPI (V/F curve) and NVML (telemetry); thread-safe.</summary>
public sealed class NvidiaGpu : IGpuControl
{
    private readonly object _sync = new();
    private readonly NvApi _api;
    private nint _gpu;
    private nint _nvmlDevice;
    private uint[] _mask;
    private bool _disposed;

    private NvidiaGpu(NvApi api, nint gpu, nint nvmlDevice, uint[] mask, GpuIdentity identity)
    {
        _api = api;
        _gpu = gpu;
        _nvmlDevice = nvmlDevice;
        _mask = mask;
        Identity = identity;
    }

    public GpuIdentity Identity { get; }

    /// <summary>True if the current process token is elevated.</summary>
    public bool CanWrite => Environment.IsPrivilegedProcess;

    /// <summary>Opens the first NVIDIA GPU and initializes NVAPI and NVML.</summary>
    /// <exception cref="GpuControlException">The driver libraries are missing or no GPU was found.</exception>
    public static NvidiaGpu OpenFirst()
    {
        var api = NvApi.Load();
        var gpus = api.EnumPhysicalGpus();
        if (gpus.Length == 0) throw new GpuControlException("No NVIDIA GPU was found.");
        var gpu = gpus[0];

        var bus = api.GetBusId(gpu);
        var (deviceId, subsystemId, revisionId) = api.GetPciIdentifiers(gpu);
        var identity = new GpuIdentity(
            api.GetFullName(gpu), api.GetDriverVersion(),
            deviceId >> 16, subsystemId, revisionId, bus);
        var mask = api.GetValidPointMask(gpu);

        var nvmlDevice = OpenNvml(bus);
        return new NvidiaGpu(api, gpu, nvmlDevice, mask, identity);
    }

    /// <summary>Returns the graphics V/F points (ascending voltage) with the driver point index.</summary>
    public StockCurve ReadStockCurve()
    {
        lock (_sync)
        {
            return VfLayout.ToStockCurve(ReadGraphicsPoints());
        }
    }

    /// <summary>Returns the current graphics offsets in kHz, in the order of <see cref="ReadStockCurve"/>.</summary>
    public CurveOffsets ReadOffsets()
    {
        lock (_sync)
        {
            return new CurveOffsets(ReadGraphicsPoints().Select(p => p.OffsetKhz).ToList());
        }
    }

    /// <summary>
    /// Writes the offsets (clamped to ±1000 MHz) into the graphics entries of a freshly read Control buffer,
    /// submits it with SetControl and reads it back; a write the driver did not take throws.
    /// </summary>
    public void ApplyOffsets(CurveOffsets offsets)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        lock (_sync)
        {
            ThrowIfDisposed();
            var status = _api.GetStatus(_gpu, _mask);
            var control = _api.GetControl(_gpu, _mask);
            var points = VfLayout.DecodeGraphicsPoints(status, control, _mask);
            if (offsets.OffsetsKhz.Count != points.Count)
                throw new ArgumentException(
                    $"Expected {points.Count} offsets but got {offsets.OffsetsKhz.Count}.", nameof(offsets));

            var patched = VfLayout.PatchGraphicsOffsets(
                control, points.Select(p => p.Index).ToList(), offsets.OffsetsKhz);
            _api.SetControl(_gpu, patched);

            var applied = VfLayout.DecodeGraphicsPoints(_api.GetStatus(_gpu, _mask), _api.GetControl(_gpu, _mask), _mask);
            var expected = offsets.OffsetsKhz.Select(VfLayout.ClampOffsetKhz).ToList();
            var byIndex = applied.ToDictionary(p => p.Index, p => p.OffsetKhz);
            for (var i = 0; i < points.Count; i++)
            {
                if (Math.Abs(byIndex[points[i].Index] - expected[i]) > 1000)
                    throw new GpuControlException(
                        $"The driver did not apply the curve (point {points[i].Index}: {byIndex[points[i].Index]} kHz instead of {expected[i]} kHz).");
            }
        }
    }

    /// <summary>Reinitializes NVAPI, finds the GPU on the same bus again and reopens NVML.</summary>
    public void Reinitialize()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            Nvml.nvmlShutdown();
            _api.Reinitialize();
            var gpu = _api.EnumPhysicalGpus().FirstOrDefault(g => _api.GetBusId(g) == Identity.BusNumber);
            if (gpu == 0) throw new GpuControlException("The NVIDIA GPU did not come back after the driver reset.");
            _gpu = gpu;
            _mask = _api.GetValidPointMask(gpu);
            _nvmlDevice = OpenNvml(Identity.BusNumber);
        }
    }

    /// <summary>Samples NVML (clocks, power, temperature, utilization, limit reasons) and the NVAPI core voltage.</summary>
    public Telemetry ReadTelemetry()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var d = _nvmlDevice;
            Nvml.Check(Nvml.nvmlDeviceGetClockInfo(d, Nvml.ClockGraphics, out var gfx), "nvmlDeviceGetClockInfo(graphics)");
            Nvml.Check(Nvml.nvmlDeviceGetClockInfo(d, Nvml.ClockMemory, out var mem), "nvmlDeviceGetClockInfo(memory)");
            Nvml.Check(Nvml.nvmlDeviceGetPowerUsage(d, out var powerMw), "nvmlDeviceGetPowerUsage");
            Nvml.Check(Nvml.nvmlDeviceGetEnforcedPowerLimit(d, out var limitMw), "nvmlDeviceGetEnforcedPowerLimit");
            Nvml.Check(Nvml.nvmlDeviceGetTemperature(d, 0, out var temp), "nvmlDeviceGetTemperature");
            Nvml.Check(Nvml.nvmlDeviceGetUtilizationRates(d, out var util), "nvmlDeviceGetUtilizationRates");
            var reasons = Nvml.GetClockEventReasons(d);
            var voltageUv = _api.GetCoreVoltageUv(_gpu);

            return new Telemetry(
                DateTimeOffset.UtcNow, (int)gfx, (int)mem, voltageUv,
                powerMw / 1000.0, limitMw / 1000.0, (int)temp,
                (int)util.Gpu, (ClockLimitReasons)reasons);
        }
    }

    /// <summary>Shuts NVML down; NVAPI stays loaded for the process lifetime.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Nvml.nvmlShutdown();
        }
    }

    private IReadOnlyList<GraphicsPoint> ReadGraphicsPoints()
    {
        ThrowIfDisposed();
        var status = _api.GetStatus(_gpu, _mask);
        var control = _api.GetControl(_gpu, _mask);
        return VfLayout.DecodeGraphicsPoints(status, control, _mask);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static nint OpenNvml(uint busNumber)
    {
        try
        {
            Nvml.Check(Nvml.nvmlInit_v2(), "nvmlInit_v2");
        }
        catch (DllNotFoundException)
        {
            throw new GpuControlException("nvml.dll was not found; an NVIDIA driver is required.");
        }

        var busId = $"00000000:{busNumber:X2}:00.0";
        if (Nvml.nvmlDeviceGetHandleByPciBusId_v2(busId, out var device) == 0) return device;
        Nvml.Check(Nvml.nvmlDeviceGetHandleByIndex_v2(0, out device), "nvmlDeviceGetHandleByIndex_v2");
        return device;
    }
}
