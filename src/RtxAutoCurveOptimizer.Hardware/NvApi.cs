using System.Runtime.InteropServices;
using System.Text;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Hardware;

/// <summary>Thin wrapper over the NVAPI functions obtained through nvapi_QueryInterface; not thread-safe by itself.</summary>
internal sealed unsafe partial class NvApi
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGetFullName = 0xCEEE8E9F;
    private const uint IdGetBusId = 0x1BE0B8E5;
    private const uint IdGetPciIdentifiers = 0x2DDFB66E;
    private const uint IdGetDriverAndBranchVersion = 0x2926AAAD;
    private const uint IdVfGetInfo = 0x507B4B59;
    private const uint IdVfGetStatus = 0x21537AD4;
    private const uint IdVfGetControl = 0x23F1B133;
    private const uint IdVfSetControl = 0x0733E009;
    private const uint IdVoltRailsGetStatus = 0x465F9BCF;

    private const int MaxGpus = 64;
    private const int ShortStringSize = 64;

    private readonly delegate* unmanaged[Cdecl]<int> _initialize;
    private readonly delegate* unmanaged[Cdecl]<nint*, uint*, int> _enumGpus;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, int> _getFullName;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, int> _getBusId;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, uint*, uint*, uint*, int> _getPciIdentifiers;
    private readonly delegate* unmanaged[Cdecl]<uint*, byte*, int> _getDriverVersion;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, int> _vfGetInfo;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, int> _vfGetStatus;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, int> _vfGetControl;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, int> _vfSetControl;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, int> _voltRailsGetStatus;

    [LibraryImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint QueryInterface(uint id);

    private NvApi()
    {
        _initialize = (delegate* unmanaged[Cdecl]<int>)Query(IdInitialize, "NvAPI_Initialize");
        _enumGpus = (delegate* unmanaged[Cdecl]<nint*, uint*, int>)Query(IdEnumPhysicalGpus, "NvAPI_EnumPhysicalGPUs");
        _getFullName = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Query(IdGetFullName, "NvAPI_GPU_GetFullName");
        _getBusId = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Query(IdGetBusId, "NvAPI_GPU_GetBusId");
        _getPciIdentifiers = (delegate* unmanaged[Cdecl]<nint, uint*, uint*, uint*, uint*, int>)Query(
            IdGetPciIdentifiers, "NvAPI_GPU_GetPCIIdentifiers");
        _getDriverVersion = (delegate* unmanaged[Cdecl]<uint*, byte*, int>)Query(
            IdGetDriverAndBranchVersion, "NvAPI_SYS_GetDriverAndBranchVersion");
        _vfGetInfo = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Query(IdVfGetInfo, "ClkVfPointsGetInfo");
        _vfGetStatus = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Query(IdVfGetStatus, "ClkVfPointsGetStatus");
        _vfGetControl = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Query(IdVfGetControl, "ClkVfPointsGetControl");
        _vfSetControl = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Query(IdVfSetControl, "ClkVfPointsSetControl");
        _voltRailsGetStatus = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Query(
            IdVoltRailsGetStatus, "ClientVoltRailsGetStatus");
    }

    /// <summary>Loads nvapi64.dll, resolves all functions and calls NvAPI_Initialize.</summary>
    public static NvApi Load()
    {
        NvApi api;
        try
        {
            api = new NvApi();
        }
        catch (DllNotFoundException)
        {
            throw new GpuControlException("nvapi64.dll was not found; an NVIDIA driver is required.");
        }
        Check(api._initialize(), "NvAPI_Initialize");
        return api;
    }

    /// <summary>Calls NvAPI_Initialize again; GPU handles must be enumerated afresh afterwards.</summary>
    public void Reinitialize() => Check(_initialize(), "NvAPI_Initialize");

    /// <summary>Maps a non-zero NVAPI status to an exception; -137 becomes <see cref="GpuAccessDeniedException"/>.</summary>
    public static void Check(int status, string function)
    {
        if (status == 0) return;
        if (status == -137)
            throw new GpuAccessDeniedException(
                $"{function} failed with NVAPI status -137 (invalid user privilege); run as administrator.");
        throw new GpuControlException($"{function} failed with NVAPI status {status}.", status);
    }

    /// <summary>Returns the physical GPU handles.</summary>
    public nint[] EnumPhysicalGpus()
    {
        var handles = stackalloc nint[MaxGpus];
        uint count;
        Check(_enumGpus(handles, &count), "NvAPI_EnumPhysicalGPUs");
        var result = new nint[Math.Min(count, MaxGpus)];
        for (var i = 0; i < result.Length; i++) result[i] = handles[i];
        return result;
    }

    /// <summary>Returns the marketing name of the GPU.</summary>
    public string GetFullName(nint gpu)
    {
        var buffer = stackalloc byte[ShortStringSize];
        Check(_getFullName(gpu, buffer), "NvAPI_GPU_GetFullName");
        return ReadAscii(buffer, ShortStringSize);
    }

    /// <summary>Returns the PCI bus number of the GPU.</summary>
    public uint GetBusId(nint gpu)
    {
        uint bus;
        Check(_getBusId(gpu, &bus), "NvAPI_GPU_GetBusId");
        return bus;
    }

    /// <summary>Returns the raw PCI identifiers (device id in the high 16 bits of the first value).</summary>
    public (uint DeviceId, uint SubsystemId, uint RevisionId) GetPciIdentifiers(nint gpu)
    {
        uint device, subsystem, revision, ext;
        Check(_getPciIdentifiers(gpu, &device, &subsystem, &revision, &ext), "NvAPI_GPU_GetPCIIdentifiers");
        return (device, subsystem, revision);
    }

    /// <summary>Returns the driver version formatted like "621.01".</summary>
    public string GetDriverVersion()
    {
        uint version;
        var branch = stackalloc byte[ShortStringSize];
        Check(_getDriverVersion(&version, branch), "NvAPI_SYS_GetDriverAndBranchVersion");
        return VfLayout.FormatDriverVersion(version);
    }

    /// <summary>Calls ClkVfPointsGetInfo with a zero mask and returns the valid-point mask.</summary>
    public uint[] GetValidPointMask(nint gpu)
    {
        var buffer = VfLayout.CreateBuffer(VfLayout.InfoSize, new uint[VfLayout.MaskWords]);
        fixed (byte* p = buffer) Check(_vfGetInfo(gpu, p), "ClkVfPointsGetInfo");
        return VfLayout.ReadMask(buffer);
    }

    /// <summary>Calls ClkVfPointsGetStatus for the given mask and returns the filled buffer.</summary>
    public byte[] GetStatus(nint gpu, ReadOnlySpan<uint> mask)
    {
        var buffer = VfLayout.CreateBuffer(VfLayout.StatusSize, mask);
        fixed (byte* p = buffer) Check(_vfGetStatus(gpu, p), "ClkVfPointsGetStatus");
        return buffer;
    }

    /// <summary>Calls ClkVfPointsGetControl for the given mask and returns the filled buffer.</summary>
    public byte[] GetControl(nint gpu, ReadOnlySpan<uint> mask)
    {
        var buffer = VfLayout.CreateBuffer(VfLayout.ControlSize, mask);
        fixed (byte* p = buffer) Check(_vfGetControl(gpu, p), "ClkVfPointsGetControl");
        return buffer;
    }

    /// <summary>Calls ClkVfPointsSetControl with a buffer previously obtained from GetControl.</summary>
    public void SetControl(nint gpu, byte[] control)
    {
        fixed (byte* p = control) Check(_vfSetControl(gpu, p), "ClkVfPointsSetControl");
    }

    /// <summary>Returns the current core voltage in µV from ClientVoltRailsGetStatus.</summary>
    public int GetCoreVoltageUv(nint gpu)
    {
        var buffer = VfLayout.CreateBuffer(VfLayout.VoltRailsSize);
        fixed (byte* p = buffer) Check(_voltRailsGetStatus(gpu, p), "ClientVoltRailsGetStatus");
        return VfLayout.DecodeVoltageUv(buffer);
    }

    private static nint Query(uint id, string name)
    {
        var fn = QueryInterface(id);
        if (fn == 0)
            throw new GpuControlException($"nvapi_QueryInterface returned no function for {name} (0x{id:X8}).");
        return fn;
    }

    private static string ReadAscii(byte* buffer, int max)
    {
        var length = 0;
        while (length < max && buffer[length] != 0) length++;
        return Encoding.ASCII.GetString(buffer, length);
    }
}
