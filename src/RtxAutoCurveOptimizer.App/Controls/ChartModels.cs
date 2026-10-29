using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.App.Controls;

/// <summary>Theme-aware color slot of a chart series.</summary>
public enum ChartColor { Stock, Current, Accent, Success, Critical, Caution }

/// <summary>One data point: X in mV, Y in MHz.</summary>
public readonly record struct ChartPoint(double X, double Y);

/// <summary>A named set of points drawn as a line, as markers, or both.</summary>
public sealed class ChartSeries
{
    public required string Name { get; init; }

    public required ChartColor Color { get; init; }

    public IReadOnlyList<ChartPoint> Points { get; init; } = [];

    public bool ShowLine { get; init; } = true;

    /// <summary>Marker diameter in device-independent pixels; 0 draws no markers.</summary>
    public double MarkerSize { get; init; }

    public bool Dashed { get; init; }

    public double Thickness { get; init; } = 2;

    public bool ShowInLegend { get; init; } = true;

    /// <summary>Builds a voltage/frequency series from a stock curve plus optional offsets.</summary>
    public static IReadOnlyList<ChartPoint> CurvePoints(StockCurve stock, CurveOffsets? offsets) =>
        [.. stock.Points.Select((p, i) =>
            new ChartPoint(p.VoltageUv / 1000.0, (p.StockKhz + (offsets is null ? 0 : offsets.OffsetsKhz[i])) / 1000.0))];

    /// <summary>Voltage range worth plotting: starts shortly below where the stock curve leaves its idle plateau.</summary>
    public static (double Min, double Max) VoltageRange(StockCurve stock)
    {
        var points = stock.Points;
        var floor = points.Min(p => p.StockKhz);
        var rising = points.FirstOrDefault(p => p.StockKhz > floor + 500_000);
        var min = rising.VoltageUv == 0 ? points[0].VoltageUv / 1000.0 : rising.VoltageUv / 1000.0 - 15;
        var max = points[^1].VoltageUv / 1000.0;
        return (Math.Floor(min / 10) * 10, Math.Ceiling(max / 50) * 50);
    }
}
