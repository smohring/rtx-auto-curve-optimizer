using System.Runtime.InteropServices;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Hardware;

/// <summary>NVML utilization rates (percent).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvmlUtilization
{
    public uint Gpu;
    public uint Memory;
}

/// <summary>P/Invoke declarations for nvml.dll plus status checking.</summary>
internal static partial class Nvml
{
    private const string Dll = "nvml.dll";

    public const uint ClockGraphics = 0;
    public const uint ClockMemory = 2;

    [LibraryImport(Dll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlInit_v2();

    [LibraryImport(Dll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlShutdown();

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlDeviceGetHandleByPciBusId_v2(string pciBusId, out nint device);

    [LibraryImport(Dll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);

    [LibraryImport(Dll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlDeviceGetPowerUsage(nint device, out uint milliwatts);

    [LibraryImport(Dll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlDeviceGetEnforcedPowerLimit(nint device, out uint milliwatts);

    [LibraryImport(Dll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlDeviceGetClockInfo(nint device, uint clockType, out uint mhz);

    [LibraryImport(Dll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlDeviceGetTemperature(nint device, uint sensor, out uint celsius);

    [LibraryImport(Dll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlDeviceGetUtilizationRates(nint device, out NvmlUtilization utilization);

    [LibraryImport(Dll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlDeviceGetCurrentClocksEventReasons(nint device, out ulong reasons);

    [LibraryImport(Dll)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int nvmlDeviceGetCurrentClocksThrottleReasons(nint device, out ulong reasons);

    /// <summary>Throws <see cref="GpuControlException"/> naming the function if the NVML status is not success (0).</summary>
    public static void Check(int status, string function)
    {
        if (status != 0)
            throw new GpuControlException($"{function} failed with NVML status {status}.", status);
    }

    /// <summary>Reads the clock limit reasons, using the pre-rename export on older drivers.</summary>
    public static ulong GetClockEventReasons(nint device)
    {
        ulong reasons;
        try
        {
            Check(nvmlDeviceGetCurrentClocksEventReasons(device, out reasons), "nvmlDeviceGetCurrentClocksEventReasons");
        }
        catch (EntryPointNotFoundException)
        {
            Check(nvmlDeviceGetCurrentClocksThrottleReasons(device, out reasons),
                "nvmlDeviceGetCurrentClocksThrottleReasons");
        }
        return reasons;
    }
}
