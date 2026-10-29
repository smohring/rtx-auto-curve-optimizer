using System.Buffers.Binary;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Afterburner;

/// <summary>One decoded point of an Afterburner VFCurve blob; voltage in mV, frequencies in MHz.</summary>
public readonly record struct VfCurvePoint(float VoltageMv, float StockMhz, float OffsetMhz)
{
    /// <summary>Stock frequency plus offset.</summary>
    public float FinalMhz => StockMhz + OffsetMhz;
}

/// <summary>
/// Codec for the <c>VFCurve</c> hex blob of an Afterburner profile: a 12-byte header
/// (0x00020000, point count, 0), then 12 bytes per point, then zero padding.
/// Parsing keeps header and padding bytes so <see cref="ToHex"/> reproduces the input exactly.
/// </summary>
public sealed class AfterburnerVfCurve
{
    /// <summary>First header word of every blob.</summary>
    public const uint Magic = 0x00020000;

    /// <summary>Blob length Afterburner writes for a 127-point curve.</summary>
    public const int DefaultLengthBytes = 3224;

    private const int HeaderBytes = 12;
    private const int PointBytes = 12;

    private readonly VfCurvePoint[] _points;
    private readonly uint _headerWord2;
    private readonly byte[] _tail;

    private AfterburnerVfCurve(VfCurvePoint[] points, uint headerWord2, byte[] tail)
    {
        _points = points;
        _headerWord2 = headerWord2;
        _tail = tail;
    }

    /// <summary>The decoded points in blob order.</summary>
    public IReadOnlyList<VfCurvePoint> Points => _points;

    /// <summary>The offsets of this curve for <paramref name="stock"/>, or null if the points do not match it (count or voltage).</summary>
    public CurveOffsets? ToOffsets(StockCurve stock)
    {
        if (_points.Length != stock.Points.Count) return null;
        for (var i = 0; i < _points.Length; i++)
            if (Math.Abs(_points[i].VoltageMv * 1000 - stock.Points[i].VoltageUv) > 1000) return null;
        return new CurveOffsets([.. _points.Select(p => (int)Math.Round(p.OffsetMhz * 1000))]);
    }

    /// <summary>Total blob length in bytes, including padding.</summary>
    public int LengthBytes => HeaderBytes + _points.Length * PointBytes + _tail.Length;

    /// <summary>Parses a hex blob (either case).</summary>
    /// <exception cref="FormatException">The text is not a valid VFCurve blob.</exception>
    public static AfterburnerVfCurve Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex.Trim());
        }
        catch (FormatException ex)
        {
            throw new FormatException("VFCurve is not a valid hex string.", ex);
        }

        if (bytes.Length < HeaderBytes)
            throw new FormatException("VFCurve is shorter than its 12-byte header.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
            throw new FormatException("VFCurve header magic is not 0x00020000.");

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        if (count > (bytes.Length - HeaderBytes) / PointBytes)
            throw new FormatException("VFCurve point count exceeds the blob length.");

        var points = new VfCurvePoint[count];
        for (int i = 0; i < points.Length; i++)
        {
            var span = bytes.AsSpan(HeaderBytes + i * PointBytes, PointBytes);
            points[i] = new VfCurvePoint(
                BinaryPrimitives.ReadSingleLittleEndian(span),
                BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[8..]));
        }

        int tailStart = HeaderBytes + points.Length * PointBytes;
        return new AfterburnerVfCurve(
            points, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), bytes[tailStart..]);
    }

    /// <summary>Parses a hex blob; returns false instead of throwing on invalid input.</summary>
    public static bool TryParse(string? hex, out AfterburnerVfCurve? curve)
    {
        curve = null;
        if (string.IsNullOrWhiteSpace(hex))
            return false;
        try
        {
            curve = Parse(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a blob from a stock curve and offsets (µV to mV, kHz to MHz), zero-padded to
    /// <paramref name="lengthBytes"/>.
    /// </summary>
    public static AfterburnerVfCurve Create(
        StockCurve stock, CurveOffsets offsets, int lengthBytes = DefaultLengthBytes)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(offsets);
        if (stock.Points.Count != offsets.OffsetsKhz.Count)
            throw new ArgumentException("Offsets and stock curve must have the same number of points.", nameof(offsets));

        int used = HeaderBytes + stock.Points.Count * PointBytes;
        if (lengthBytes < used)
            throw new ArgumentOutOfRangeException(nameof(lengthBytes), $"A blob for {stock.Points.Count} points needs at least {used} bytes.");

        var points = new VfCurvePoint[stock.Points.Count];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new VfCurvePoint(
                stock.Points[i].VoltageUv / 1000f,
                stock.Points[i].StockKhz / 1000f,
                offsets.OffsetsKhz[i] / 1000f);
        }

        return new AfterburnerVfCurve(points, 0, new byte[lengthBytes - used]);
    }

    /// <summary>Serializes to the uppercase hex string stored in the profile.</summary>
    public string ToHex()
    {
        var bytes = new byte[LengthBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)_points.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), _headerWord2);
        for (int i = 0; i < _points.Length; i++)
        {
            var span = bytes.AsSpan(HeaderBytes + i * PointBytes, PointBytes);
            BinaryPrimitives.WriteSingleLittleEndian(span, _points[i].VoltageMv);
            BinaryPrimitives.WriteSingleLittleEndian(span[4..], _points[i].StockMhz);
            BinaryPrimitives.WriteSingleLittleEndian(span[8..], _points[i].OffsetMhz);
        }
        _tail.CopyTo(bytes, HeaderBytes + _points.Length * PointBytes);
        return Convert.ToHexString(bytes);
    }
}
