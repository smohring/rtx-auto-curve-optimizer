using System.Buffers.Binary;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Hardware;

/// <summary>One decoded ClkVfPointsGetStatus entry.</summary>
public readonly record struct StatusPoint(int Index, uint Type, int FrequencyKhz, int VoltageUv);

/// <summary>One decoded ClkVfPointsGetControl entry.</summary>
public readonly record struct ControlPoint(int Index, uint Type, int OffsetKhz);

/// <summary>A graphics point joined from Status and Control: effective frequency, offset and derived stock.</summary>
public readonly record struct GraphicsPoint(int Index, int VoltageUv, int StatusKhz, int OffsetKhz)
{
    /// <summary>Stock frequency = effective frequency minus offset.</summary>
    public int StockKhz => StatusKhz - OffsetKhz;
}

/// <summary>Pure byte-layout logic for the NVAPI ClkVfPoints structs; no native calls.</summary>
public static class VfLayout
{
    public const int Version = 1;
    public const int InfoSize = 0x182C;
    public const int StatusSize = 0x1C28;
    public const int ControlSize = 0x2420;
    public const int VoltRailsSize = 0x4C;

    public const int HeaderSize = 68;
    public const int MaskOffset = 4;
    public const int MaskWords = 8;
    public const int StatusEntrySize = 28;
    public const int ControlEntrySize = 36;
    public const int ControlOffsetField = 20;
    public const int VoltRailsVoltageField = 40;

    public const uint TypeGraphics = 0;

    public const int MaxOffsetKhz = 1_000_000;

    /// <summary>Version field value: size in the low 16 bits, struct version in the high 16 bits.</summary>
    public static uint VersionField(int size, int version = Version) => (uint)size | ((uint)version << 16);

    /// <summary>Allocates a zeroed buffer with only the version field set (structs without a point mask).</summary>
    public static byte[] CreateBuffer(int size)
    {
        var buffer = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, VersionField(size));
        return buffer;
    }

    /// <summary>Allocates a zeroed buffer with the version field set and the given point mask in the header.</summary>
    public static byte[] CreateBuffer(int size, ReadOnlySpan<uint> mask)
    {
        if (mask.Length != MaskWords)
            throw new ArgumentException($"Mask must have {MaskWords} words.", nameof(mask));
        var buffer = CreateBuffer(size);
        WriteMask(buffer, mask);
        return buffer;
    }

    /// <summary>Reads the 256-bit point mask from the header.</summary>
    public static uint[] ReadMask(ReadOnlySpan<byte> buffer)
    {
        var mask = new uint[MaskWords];
        for (var i = 0; i < MaskWords; i++)
            mask[i] = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(MaskOffset + 4 * i)..]);
        return mask;
    }

    /// <summary>Writes the point mask into the header.</summary>
    public static void WriteMask(Span<byte> buffer, ReadOnlySpan<uint> mask)
    {
        for (var i = 0; i < MaskWords; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[(MaskOffset + 4 * i)..], mask[i]);
    }

    /// <summary>True if bit <paramref name="index"/> is set in the mask.</summary>
    public static bool IsPointSet(ReadOnlySpan<uint> mask, int index) =>
        index >= 0 && index < MaskWords * 32 && (mask[index / 32] & (1u << (index % 32))) != 0;

    /// <summary>Byte offset of point <paramref name="index"/> within a struct with the given entry size.</summary>
    public static int EntryOffset(int index, int entrySize) => HeaderSize + index * entrySize;

    /// <summary>Decodes all masked entries of a Status buffer.</summary>
    public static IReadOnlyList<StatusPoint> DecodeStatus(ReadOnlySpan<byte> status, ReadOnlySpan<uint> mask)
    {
        var points = new List<StatusPoint>();
        for (var i = 0; i < MaskWords * 32; i++)
        {
            if (!IsPointSet(mask, i)) continue;
            var o = RequireEntry(status, i, StatusEntrySize);
            points.Add(new StatusPoint(
                i,
                BinaryPrimitives.ReadUInt32LittleEndian(status[o..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(status[(o + 4)..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(status[(o + 8)..])));
        }
        return points;
    }

    /// <summary>Decodes all masked entries of a Control buffer (type and frequency offset).</summary>
    public static IReadOnlyList<ControlPoint> DecodeControl(ReadOnlySpan<byte> control, ReadOnlySpan<uint> mask)
    {
        var points = new List<ControlPoint>();
        for (var i = 0; i < MaskWords * 32; i++)
        {
            if (!IsPointSet(mask, i)) continue;
            var o = RequireEntry(control, i, ControlEntrySize);
            points.Add(new ControlPoint(
                i,
                BinaryPrimitives.ReadUInt32LittleEndian(control[o..]),
                BinaryPrimitives.ReadInt32LittleEndian(control[(o + ControlOffsetField)..])));
        }
        return points;
    }

    /// <summary>
    /// Joins Status and Control into the graphics points, ascending by voltage then index. The graphics curve is
    /// the leading run of type-0 entries: the driver also reports memory points with type 0 after the first
    /// memory entry (points 130/131 on the dev machine), which must never be treated as graphics.
    /// </summary>
    public static IReadOnlyList<GraphicsPoint> DecodeGraphicsPoints(
        ReadOnlySpan<byte> status, ReadOnlySpan<byte> control, ReadOnlySpan<uint> mask)
    {
        var offsetByIndex = DecodeControl(control, mask).ToDictionary(c => c.Index);
        var result = new List<GraphicsPoint>();
        foreach (var s in DecodeStatus(status, mask))
        {
            if (s.Type != TypeGraphics) break;
            var c = offsetByIndex[s.Index];
            if (c.Type != TypeGraphics)
                throw new GpuControlException($"Point {s.Index} is graphics in Status but type {c.Type} in Control.");
            result.Add(new GraphicsPoint(s.Index, s.VoltageUv, s.FrequencyKhz, c.OffsetKhz));
        }
        return result.OrderBy(p => p.VoltageUv).ThenBy(p => p.Index).ToList();
    }

    /// <summary>Builds the stock curve (index, voltage, stock frequency) from graphics points.</summary>
    public static StockCurve ToStockCurve(IReadOnlyList<GraphicsPoint> points) =>
        new(points.Select(p => new CurvePoint(p.Index, p.VoltageUv, p.StockKhz)).ToList());

    /// <summary>Clamps an offset to the allowed range of -1000 to +1000 MHz.</summary>
    public static int ClampOffsetKhz(int offsetKhz) => Math.Clamp(offsetKhz, -MaxOffsetKhz, MaxOffsetKhz);

    /// <summary>
    /// Returns a copy of a Control buffer with the offsets of the listed graphics points replaced (clamped);
    /// every other byte, including memory entries, is preserved.
    /// </summary>
    public static byte[] PatchGraphicsOffsets(
        ReadOnlySpan<byte> control, IReadOnlyList<int> pointIndices, IReadOnlyList<int> offsetsKhz)
    {
        if (pointIndices.Count != offsetsKhz.Count)
            throw new ArgumentException(
                $"Expected {pointIndices.Count} offsets but got {offsetsKhz.Count}.", nameof(offsetsKhz));
        var patched = control.ToArray();
        for (var n = 0; n < pointIndices.Count; n++)
        {
            var o = RequireEntry(patched, pointIndices[n], ControlEntrySize);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(o));
            if (type != TypeGraphics)
                throw new ArgumentException($"Point {pointIndices[n]} is not a graphics point (type {type}).");
            BinaryPrimitives.WriteInt32LittleEndian(
                patched.AsSpan(o + ControlOffsetField), ClampOffsetKhz(offsetsKhz[n]));
        }
        return patched;
    }

    /// <summary>Reads the core voltage (µV) from a ClientVoltRailsGetStatus buffer.</summary>
    public static int DecodeVoltageUv(ReadOnlySpan<byte> voltRails) =>
        (int)BinaryPrimitives.ReadUInt32LittleEndian(voltRails[VoltRailsVoltageField..]);

    /// <summary>Formats the NVAPI driver version (62101) as "621.01".</summary>
    public static string FormatDriverVersion(uint raw) => $"{raw / 100}.{raw % 100:D2}";

    private static int RequireEntry(ReadOnlySpan<byte> buffer, int index, int entrySize)
    {
        var o = EntryOffset(index, entrySize);
        if (index < 0 || o + entrySize > buffer.Length)
            throw new ArgumentException($"Point {index} lies outside the {buffer.Length}-byte buffer.");
        return o;
    }
}
