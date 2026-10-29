namespace RtxAutoCurveOptimizer.Core;

/// <summary>One graphics V/F point: driver index, voltage in µV, stock frequency in kHz.</summary>
public readonly record struct CurvePoint(int Index, int VoltageUv, int StockKhz);

/// <summary>Graphics V/F points in ascending voltage order; Index is the driver point index.</summary>
public sealed record StockCurve(IReadOnlyList<CurvePoint> Points);

/// <summary>One frequency offset (kHz) per <see cref="StockCurve"/> point, same order.</summary>
public sealed record CurveOffsets(IReadOnlyList<int> OffsetsKhz)
{
    /// <summary>Creates offsets of zero for <paramref name="count"/> points.</summary>
    public static CurveOffsets Zero(int count) => new(new int[count]);
}
