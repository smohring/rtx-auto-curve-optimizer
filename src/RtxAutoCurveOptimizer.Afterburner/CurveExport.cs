using System.Globalization;
using System.Text;
using System.Text.Json;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Afterburner;

/// <summary>Exports a curve (stock plus offsets) as JSON or CSV with voltages in mV and frequencies in MHz.</summary>
public static class CurveExport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>CSV with header <c>index,voltage_mv,stock_mhz,offset_mhz,final_mhz</c>, invariant culture, CRLF lines.</summary>
    public static string ToCsv(StockCurve stock, CurveOffsets offsets)
    {
        var rows = Rows(stock, offsets);
        var sb = new StringBuilder("index,voltage_mv,stock_mhz,offset_mhz,final_mhz\r\n");
        foreach (var r in rows)
        {
            sb.Append(r.Index.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Num(r.VoltageMv)).Append(',')
              .Append(Num(r.StockMhz)).Append(',')
              .Append(Num(r.OffsetMhz)).Append(',')
              .Append(Num(r.FinalMhz)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>JSON object <c>{ "points": [ { index, voltageMv, stockMhz, offsetMhz, finalMhz } ] }</c>.</summary>
    public static string ToJson(StockCurve stock, CurveOffsets offsets)
    {
        var points = Rows(stock, offsets).Select(r => new
        {
            index = r.Index,
            voltageMv = r.VoltageMv,
            stockMhz = r.StockMhz,
            offsetMhz = r.OffsetMhz,
            finalMhz = r.FinalMhz,
        });
        return JsonSerializer.Serialize(new { points }, JsonOptions);
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static List<(int Index, double VoltageMv, double StockMhz, double OffsetMhz, double FinalMhz)> Rows(
        StockCurve stock, CurveOffsets offsets)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(offsets);
        if (stock.Points.Count != offsets.OffsetsKhz.Count)
            throw new ArgumentException("Offsets and stock curve must have the same number of points.", nameof(offsets));

        var rows = new List<(int, double, double, double, double)>(stock.Points.Count);
        for (int i = 0; i < stock.Points.Count; i++)
        {
            var p = stock.Points[i];
            int offset = offsets.OffsetsKhz[i];
            rows.Add((p.Index, p.VoltageUv / 1000.0, p.StockKhz / 1000.0, offset / 1000.0, (p.StockKhz + offset) / 1000.0));
        }
        return rows;
    }
}
