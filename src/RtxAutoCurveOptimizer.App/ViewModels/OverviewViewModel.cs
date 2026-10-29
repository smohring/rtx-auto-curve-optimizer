using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using RtxAutoCurveOptimizer.Afterburner;
using RtxAutoCurveOptimizer.App.Controls;
using RtxAutoCurveOptimizer.App.Services;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.App.ViewModels;

/// <summary>Overview page: GPU identity, live telemetry (1 s while the page is visible), warnings and the V/F chart.</summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private const int OffsetsEveryTicks = 5;

    private readonly GpuSession session;
    private DispatcherQueueTimer? timer;
    private bool ticking;
    private int tick;
    private CurveOffsets? offsets;
    private Telemetry? latest;

    public OverviewViewModel(GpuSession session)
    {
        this.session = session;
        Tiles = new TelemetryTiles();
        GpuName = string.Empty;
        DriverText = string.Empty;
        ErrorText = string.Empty;
        session.SessionChanged += Refresh;
        Refresh();
    }

    public TelemetryTiles Tiles { get; }

    [ObservableProperty]
    public partial string GpuName { get; set; }

    [ObservableProperty]
    public partial string DriverText { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; }

    [ObservableProperty]
    public partial bool HasGpu { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    [ObservableProperty]
    public partial bool AfterburnerRunning { get; set; }

    [ObservableProperty]
    public partial bool IsDemo { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ChartSeries>? Series { get; set; }

    [ObservableProperty]
    public partial double ChartXMin { get; set; }

    [ObservableProperty]
    public partial double ChartXMax { get; set; }

    /// <summary>Reads identity, curve and capabilities again after the GPU session changed.</summary>
    private void Refresh()
    {
        var gpu = session.Gpu;
        HasGpu = gpu is not null;
        HasError = gpu is null;
        ErrorText = session.GpuError ?? string.Empty;
        IsDemo = session.DemoMode;
        IsReadOnly = gpu is not null && !gpu.CanWrite;
        GpuName = gpu?.Identity.Name ?? Loc.Get("Overview_NoGpu");
        DriverText = gpu is null ? string.Empty : Loc.Format("Overview_Driver", gpu.Identity.DriverVersion);
        Tiles.Clear();
        offsets = null;
        try
        {
            offsets = gpu?.ReadOffsets();
        }
        catch (GpuControlException ex)
        {
            ErrorText = ex.Message;
            HasError = true;
        }

        latest = null;
        BuildChart();
    }

    /// <summary>Starts the 1 s sampling timer; call when the page becomes visible.</summary>
    public void StartSampling()
    {
        if (timer is not null) return;
        tick = 0;
        timer = session.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => _ = SampleAsync();
        timer.Start();
        _ = SampleAsync();
    }

    /// <summary>Stops sampling; call when the page is hidden.</summary>
    public void StopSampling()
    {
        timer?.Stop();
        timer = null;
    }

    private async Task SampleAsync()
    {
        if (ticking) return;
        ticking = true;
        try
        {
            var gpu = session.Gpu;
            var checkOffsets = tick++ % OffsetsEveryTicks == 0;
            var (sample, current, afterburner) = await Task.Run(() =>
            {
                Telemetry? t = null;
                CurveOffsets? o = null;
                try
                {
                    t = gpu?.ReadTelemetry();
                    if (checkOffsets) o = gpu?.ReadOffsets();
                }
                catch (GpuControlException)
                {
                }

                return (t, o, AfterburnerProcess.IsRunning());
            });

            if (!ReferenceEquals(gpu, session.Gpu)) return;
            AfterburnerRunning = afterburner;
            IsReadOnly = gpu is not null && !gpu.CanWrite;
            if (sample is { } s)
            {
                latest = s;
                Tiles.Update(s);
            }

            if (current is not null && !current.OffsetsKhz.SequenceEqual(offsets?.OffsetsKhz ?? []))
                offsets = current;
            BuildChart();
        }
        finally
        {
            ticking = false;
        }
    }

    private void BuildChart()
    {
        if (session.Stock is not { } stock)
        {
            Series = null;
            return;
        }

        (ChartXMin, ChartXMax) = ChartSeries.VoltageRange(stock);
        List<ChartSeries> series =
        [
            new() { Name = Loc.Get("Chart_Stock"), Color = ChartColor.Stock, Points = ChartSeries.CurvePoints(stock, null) },
            new()
            {
                Name = Loc.Get("Chart_Current"), Color = ChartColor.Accent,
                Points = ChartSeries.CurvePoints(stock, offsets),
            },
        ];
        if (latest is { } t && t.VoltageUv > 0)
            series.Add(new ChartSeries
            {
                Name = Loc.Get("Chart_Now"), Color = ChartColor.Caution, ShowLine = false, MarkerSize = 11,
                Points = [new ChartPoint(t.VoltageUv / 1000.0, t.GraphicsClockMhz)],
            });
        Series = series;
    }
}
