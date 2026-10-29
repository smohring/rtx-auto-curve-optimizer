using System.Runtime.InteropServices;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Hardware;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary>Raw ClkVfPoints Status and Control buffers of all points, captured read-only.</summary>
internal sealed class RawVfSnapshot(byte[] status, byte[] control, uint[] mask)
{
    private readonly byte[] _status = status;
    private readonly byte[] _control = control;
    private readonly uint[] _mask = mask;

    /// <summary>Indices after the leading run of type-0 entries: memory points, including the type-0 ones at the end.</summary>
    public IReadOnlyList<int> NonGraphicsIndices() =>
        VfLayout.DecodeStatus(_status, _mask).SkipWhile(p => p.Type == VfLayout.TypeGraphics).Select(p => p.Index).ToList();

    /// <summary>One line per non-graphics point: type, memory offset, effective frequency, voltage.</summary>
    public IEnumerable<string> Describe()
    {
        var status = VfLayout.DecodeStatus(_status, _mask).ToDictionary(p => p.Index);
        var control = VfLayout.DecodeControl(_control, _mask).ToDictionary(p => p.Index);
        foreach (var i in NonGraphicsIndices())
            yield return $"point {i}: type {status[i].Type}, offset {Shared.Mhz(control[i].OffsetKhz)} MHz, " +
                         $"effective {Shared.Mhz(status[i].FrequencyKhz)} MHz at {Shared.Mv(status[i].VoltageUv)} mV";
    }

    /// <summary>Differences in the non-graphics points (every byte of their Control and Status entries); empty if identical.</summary>
    public static IReadOnlyList<string> DiffNonGraphics(RawVfSnapshot before, RawVfSnapshot after)
    {
        var diffs = new List<string>();
        if (!before._mask.SequenceEqual(after._mask)) diffs.Add("valid-point mask changed");
        var indices = before.NonGraphicsIndices();
        if (!indices.SequenceEqual(after.NonGraphicsIndices()))
        {
            diffs.Add("set of non-graphics points changed");
            return diffs;
        }

        var statusBefore = VfLayout.DecodeStatus(before._status, before._mask).ToDictionary(p => p.Index);
        var statusAfter = VfLayout.DecodeStatus(after._status, after._mask).ToDictionary(p => p.Index);
        var controlBefore = VfLayout.DecodeControl(before._control, before._mask).ToDictionary(p => p.Index);
        var controlAfter = VfLayout.DecodeControl(after._control, after._mask).ToDictionary(p => p.Index);
        foreach (var i in indices)
        {
            var c = VfLayout.EntryOffset(i, VfLayout.ControlEntrySize);
            if (!before._control.AsSpan(c, VfLayout.ControlEntrySize).SequenceEqual(after._control.AsSpan(c, VfLayout.ControlEntrySize)))
                diffs.Add($"point {i} control entry changed (type {controlBefore[i].Type} -> {controlAfter[i].Type}, " +
                          $"offset {controlBefore[i].OffsetKhz} -> {controlAfter[i].OffsetKhz} kHz, other bytes may differ too)");
            var s = VfLayout.EntryOffset(i, VfLayout.StatusEntrySize);
            if (!before._status.AsSpan(s, VfLayout.StatusEntrySize).SequenceEqual(after._status.AsSpan(s, VfLayout.StatusEntrySize)))
                diffs.Add($"point {i} status entry changed (frequency {statusBefore[i].FrequencyKhz} -> {statusAfter[i].FrequencyKhz} kHz, " +
                          $"voltage {statusBefore[i].VoltageUv} -> {statusAfter[i].VoltageUv} uV)");
        }

        return diffs;
    }
}

/// <summary>
/// Read-only access to the raw ClkVfPoints buffers. The Hardware project keeps its NVAPI wrapper internal, so this
/// resolves only the Initialize, EnumPhysicalGPUs, GetInfo, GetStatus and GetControl functions itself; SetControl is never resolved.
/// </summary>
internal sealed class RawVfReader
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdVfGetInfo = 0x507B4B59;
    private const uint IdVfGetStatus = 0x21537AD4;
    private const uint IdVfGetControl = 0x23F1B133;

    private readonly nint _gpu;
    private readonly uint[] _mask;
    private readonly VfCall _getStatus;
    private readonly VfCall _getControl;

    private RawVfReader(nint gpu, uint[] mask, VfCall getStatus, VfCall getControl)
    {
        _gpu = gpu;
        _mask = mask;
        _getStatus = getStatus;
        _getControl = getControl;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Initialize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumGpus([In, Out] nint[] handles, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VfCall(nint gpu, [In, Out] byte[] buffer);

    /// <summary>Opens the first NVIDIA GPU, the same one <see cref="NvidiaGpu.OpenFirst"/> selects.</summary>
    public static RawVfReader Open()
    {
        var library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"));
        var query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(library, "nvapi_QueryInterface"));

        T Resolve<T>(uint id) where T : Delegate
        {
            var pointer = query(id);
            if (pointer == 0) throw new GpuControlException($"nvapi_QueryInterface returned no function for 0x{id:X8}.");
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        Check(Resolve<Initialize>(IdInitialize)(), "NvAPI_Initialize");
        var handles = new nint[64];
        Check(Resolve<EnumGpus>(IdEnumPhysicalGpus)(handles, out var count), "NvAPI_EnumPhysicalGPUs");
        if (count == 0) throw new GpuControlException("No NVIDIA GPU was found.");

        var info = VfLayout.CreateBuffer(VfLayout.InfoSize, new uint[VfLayout.MaskWords]);
        Check(Resolve<VfCall>(IdVfGetInfo)(handles[0], info), "ClkVfPointsGetInfo");
        return new RawVfReader(handles[0], VfLayout.ReadMask(info), Resolve<VfCall>(IdVfGetStatus), Resolve<VfCall>(IdVfGetControl));
    }

    /// <summary>Reads Status and Control of all valid points.</summary>
    public RawVfSnapshot Capture()
    {
        var status = VfLayout.CreateBuffer(VfLayout.StatusSize, _mask);
        Check(_getStatus(_gpu, status), "ClkVfPointsGetStatus");
        var control = VfLayout.CreateBuffer(VfLayout.ControlSize, _mask);
        Check(_getControl(_gpu, control), "ClkVfPointsGetControl");
        return new RawVfSnapshot(status, control, _mask);
    }

    private static void Check(int status, string function)
    {
        if (status != 0) throw new GpuControlException($"{function} failed with NVAPI status {status}.", status);
    }
}
