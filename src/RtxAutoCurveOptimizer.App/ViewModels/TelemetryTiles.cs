using RtxAutoCurveOptimizer.App.Services;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.App.ViewModels;

/// <summary>The six live telemetry tiles shared by the Overview and Optimize pages.</summary>
public sealed class TelemetryTiles
{
    public TelemetryTiles()
    {
        Clock = new TileViewModel(Loc.Get("Tile_Clock"));
        Voltage = new TileViewModel(Loc.Get("Tile_Voltage"));
        Power = new TileViewModel(Loc.Get("Tile_Power"));
        Temperature = new TileViewModel(Loc.Get("Tile_Temperature"));
        Utilization = new TileViewModel(Loc.Get("Tile_Utilization"));
        Limits = new TileViewModel(Loc.Get("Tile_Limits"));
        All = [Clock, Voltage, Power, Temperature, Utilization, Limits];
    }

    public TileViewModel Clock { get; }

    public TileViewModel Voltage { get; }

    public TileViewModel Power { get; }

    public TileViewModel Temperature { get; }

    public TileViewModel Utilization { get; }

    public TileViewModel Limits { get; }

    public IReadOnlyList<TileViewModel> All { get; }

    /// <summary>Shows one telemetry sample.</summary>
    public void Update(Telemetry t)
    {
        Clock.Value = Fmt.Mhz(t.GraphicsClockMhz);
        Clock.Detail = Loc.Format("Tile_MemoryDetail", Fmt.Mhz(t.MemoryClockMhz));
        Voltage.Value = Fmt.UvAsMv(t.VoltageUv);
        Power.Value = Fmt.Watts(t.PowerW);
        Power.Detail = Loc.Format("Tile_PowerLimitDetail", Fmt.Watts(t.PowerLimitW));
        Temperature.Value = Fmt.Celsius(t.TemperatureC);
        Utilization.Value = string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0} %", t.UtilizationPercent);
        var reasons = Fmt.LimitReasonNames(t.LimitReasons);
        Limits.Value = Fmt.LimitReasonsShort(reasons);
        Limits.ToolTip = string.Join(", ", reasons);
    }

    public void Clear()
    {
        foreach (var tile in All) tile.Clear();
    }
}
