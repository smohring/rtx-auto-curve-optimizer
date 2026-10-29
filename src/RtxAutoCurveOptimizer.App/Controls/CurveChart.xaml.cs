using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RtxAutoCurveOptimizer.App.Services;
using Windows.Foundation;

namespace RtxAutoCurveOptimizer.App.Controls;

/// <summary>
/// Line/marker chart of frequency (MHz) over voltage (mV) drawn with XAML shapes: gridlines, axis labels,
/// legend and a pointer tooltip. Colors come from theme resources, so it follows light and dark mode.
/// </summary>
public sealed partial class CurveChart : UserControl
{
    private const double PlotLeft = 60, PlotRight = 16, PlotTop = 10, PlotBottom = 42, HoverRadius = 28;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(CurveChart),
        new PropertyMetadata(null, (d, _) => ((CurveChart)d).OnSeriesChanged()));

    public static readonly DependencyProperty XMinProperty = DependencyProperty.Register(
        nameof(XMin), typeof(double), typeof(CurveChart),
        new PropertyMetadata(double.NaN, (d, _) => ((CurveChart)d).Invalidate()));

    public static readonly DependencyProperty XMaxProperty = DependencyProperty.Register(
        nameof(XMax), typeof(double), typeof(CurveChart),
        new PropertyMetadata(double.NaN, (d, _) => ((CurveChart)d).Invalidate()));

    private readonly Canvas hover = new() { IsHitTestVisible = false };
    private bool redrawPending;
    private double xMin, xMax, yMin, yMax, plotWidth, plotHeight;
    private bool hasLayout;

    public CurveChart()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Invalidate();
    }

    /// <summary>Series to draw; replace the list to update the chart.</summary>
    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>Left end of the voltage axis in mV; NaN fits the data.</summary>
    public double XMin
    {
        get => (double)GetValue(XMinProperty);
        set => SetValue(XMinProperty, value);
    }

    /// <summary>Right end of the voltage axis in mV; NaN fits the data.</summary>
    public double XMax
    {
        get => (double)GetValue(XMaxProperty);
        set => SetValue(XMaxProperty, value);
    }

    /// <summary>Schedules one redraw on the UI thread; repeated calls are coalesced.</summary>
    public void Invalidate()
    {
        if (redrawPending) return;
        redrawPending = true;
        DispatcherQueue.TryEnqueue(Redraw);
    }

    private void OnSeriesChanged()
    {
        RebuildLegend();
        Invalidate();
    }

    private void Plot_SizeChanged(object sender, SizeChangedEventArgs e) => Invalidate();

    private Brush BrushFor(ChartColor color) => color == ChartColor.Accent ? AccentProbe.Fill : Named(color switch
    {
        ChartColor.Stock => "StockBrush",
        ChartColor.Current => "CurrentBrush",
        ChartColor.Success => "SuccessBrush",
        ChartColor.Critical => "CriticalBrush",
        _ => "CautionBrush",
    });

    private Brush Named(string key) => (Brush)Resources[key];

    private void RebuildLegend()
    {
        LegendPanel.Children.Clear();
        foreach (var s in Series ?? [])
        {
            if (!s.ShowInLegend) continue;
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            FrameworkElement swatch = s.ShowLine
                ? new Rectangle { Width = 16, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = BrushFor(s.Color), VerticalAlignment = VerticalAlignment.Center }
                : new Ellipse { Width = 9, Height = 9, Fill = BrushFor(s.Color), VerticalAlignment = VerticalAlignment.Center };
            item.Children.Add(swatch);
            item.Children.Add(new TextBlock
            {
                Text = s.Name, FontSize = 12, Foreground = Named("AxisBrush"), VerticalAlignment = VerticalAlignment.Center,
            });
            LegendPanel.Children.Add(item);
        }
    }

    private void Redraw()
    {
        redrawPending = false;
        Plot.Children.Clear();
        hasLayout = false;
        double w = Plot.ActualWidth, h = Plot.ActualHeight;
        if (w < 160 || h < 120) return;

        var series = Series ?? [];
        var all = series.SelectMany(s => s.Points).ToList();
        xMin = double.IsNaN(XMin) ? (all.Count > 0 ? all.Min(p => p.X) : 0) : XMin;
        xMax = double.IsNaN(XMax) ? (all.Count > 0 ? all.Max(p => p.X) : 1) : XMax;
        if (xMax <= xMin) xMax = xMin + 1;

        var visible = all.Where(p => p.X >= xMin && p.X <= xMax).Select(p => p.Y).ToList();
        double lo = 0, hi = 1;
        if (visible.Count > 0)
        {
            lo = visible.Min();
            hi = visible.Max();
            var pad = Math.Max((hi - lo) * 0.05, 1);
            lo -= pad;
            hi += pad;
        }

        plotWidth = w - PlotLeft - PlotRight;
        plotHeight = h - PlotTop - PlotBottom;
        var yStep = NiceStep(hi - lo, Math.Max(3, (int)(plotHeight / 40)));
        yMin = Math.Floor(lo / yStep) * yStep;
        yMax = Math.Ceiling(hi / yStep) * yStep;
        var xStep = NiceStep(xMax - xMin, Math.Max(3, (int)(plotWidth / 64)));
        hasLayout = true;

        DrawGrid(w, h, xStep, yStep);
        DrawSeries(series);
        Plot.Children.Add(hover);
    }

    private void DrawGrid(double w, double h, double xStep, double yStep)
    {
        var grid = Named("GridBrush");
        var axis = Named("AxisBrush");

        for (var y = yMin; y <= yMax + yStep * 0.01; y += yStep)
        {
            var py = MapY(y);
            AddLine(PlotLeft, py, w - PlotRight, py, grid);
            AddLabel(y.ToString("0", CultureInfo.CurrentCulture), PlotLeft - 8, py, axis, alignRight: true, centerVertically: true);
        }

        for (var x = Math.Ceiling(xMin / xStep) * xStep; x <= xMax + xStep * 0.01; x += xStep)
        {
            var px = MapX(x);
            AddLine(px, PlotTop, px, PlotTop + plotHeight, grid);
            AddLabel(x.ToString("0", CultureInfo.CurrentCulture), px, PlotTop + plotHeight + 6, axis, alignCenter: true);
        }

        var xTitle = new TextBlock { Text = Loc.Get("Chart_VoltageAxis"), FontSize = 12, Foreground = axis };
        xTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(xTitle, PlotLeft + (plotWidth - xTitle.DesiredSize.Width) / 2);
        Canvas.SetTop(xTitle, h - 20);
        Plot.Children.Add(xTitle);

        var yTitle = new TextBlock
        {
            Text = Loc.Get("Chart_FrequencyAxis"), FontSize = 12, Foreground = axis,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform { Angle = -90 },
        };
        yTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(yTitle, 10 - yTitle.DesiredSize.Width / 2);
        Canvas.SetTop(yTitle, PlotTop + (plotHeight - yTitle.DesiredSize.Height) / 2);
        Plot.Children.Add(yTitle);
    }

    private void DrawSeries(IReadOnlyList<ChartSeries> series)
    {
        var layer = new Canvas
        {
            Width = Plot.ActualWidth, Height = Plot.ActualHeight, IsHitTestVisible = false,
            Clip = new RectangleGeometry { Rect = new Rect(PlotLeft, PlotTop, plotWidth, plotHeight) },
        };

        foreach (var s in series)
        {
            var brush = BrushFor(s.Color);
            if (s.ShowLine && s.Points.Count > 1)
            {
                var line = new Polyline
                {
                    Stroke = brush, StrokeThickness = s.Thickness, StrokeLineJoin = PenLineJoin.Round,
                    Points = [.. s.Points.Select(p => new Point(MapX(p.X), MapY(p.Y)))],
                };
                if (s.Dashed) line.StrokeDashArray = [4, 3];
                layer.Children.Add(line);
            }

            if (s.MarkerSize <= 0) continue;
            foreach (var p in s.Points.Where(p => p.X >= xMin && p.X <= xMax))
            {
                var dot = new Ellipse { Width = s.MarkerSize, Height = s.MarkerSize, Fill = brush };
                Canvas.SetLeft(dot, MapX(p.X) - s.MarkerSize / 2);
                Canvas.SetTop(dot, MapY(p.Y) - s.MarkerSize / 2);
                layer.Children.Add(dot);
            }
        }

        Plot.Children.Add(layer);
    }

    private double MapX(double x) => PlotLeft + (x - xMin) / (xMax - xMin) * plotWidth;

    private double MapY(double y) => PlotTop + (yMax - y) / (yMax - yMin) * plotHeight;

    private void AddLine(double x1, double y1, double x2, double y2, Brush stroke) =>
        Plot.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = 1 });

    private void AddLabel(string text, double x, double y, Brush brush, bool alignRight = false, bool alignCenter = false, bool centerVertically = false)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, Foreground = brush };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = alignRight ? x - tb.DesiredSize.Width : alignCenter ? x - tb.DesiredSize.Width / 2 : x;
        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, centerVertically ? y - tb.DesiredSize.Height / 2 : y);
        Plot.Children.Add(tb);
    }

    /// <summary>Largest 1/2/5 x 10^n step that yields at most about <paramref name="maxTicks"/> ticks.</summary>
    private static double NiceStep(double range, int maxTicks)
    {
        var raw = range / Math.Max(1, maxTicks);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var m in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (m * magnitude >= raw) return m * magnitude;
        return 10 * magnitude;
    }

    /// <summary>Raised when the plot is clicked, with the clicked position in data units (X, Y).</summary>
    public event Action<double, double>? PlotClicked;

    private void Plot_PointerExited(object sender, PointerRoutedEventArgs e) => hover.Children.Clear();

    private void Plot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (plotWidth <= 0 || plotHeight <= 0) return;
        var pos = e.GetCurrentPoint(Plot).Position;
        PlotClicked?.Invoke(
            xMin + (pos.X - PlotLeft) / plotWidth * (xMax - xMin),
            yMax - (pos.Y - PlotTop) / plotHeight * (yMax - yMin));
    }

    private void Plot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        hover.Children.Clear();
        if (!hasLayout) return;
        var pos = e.GetCurrentPoint(Plot).Position;        if (pos.X < PlotLeft || pos.X > PlotLeft + plotWidth || pos.Y < PlotTop || pos.Y > PlotTop + plotHeight) return;

        var hits = new List<(ChartSeries Series, ChartPoint Point)>();
        foreach (var s in Series ?? [])
        {
            if (s.Points.Count == 0) continue;
            var best = s.Points.MinBy(p => Math.Abs(MapX(p.X) - pos.X));
            if (Math.Abs(MapX(best.X) - pos.X) <= HoverRadius && best.X >= xMin && best.X <= xMax) hits.Add((s, best));
        }

        if (hits.Count == 0) return;
        var anchor = hits.MinBy(h => Math.Abs(MapX(h.Point.X) - pos.X)).Point.X;
        var ax = MapX(anchor);
        hits = [.. hits.Where(h => Math.Abs(MapX(h.Point.X) - ax) <= 10)];
        hover.Children.Add(new Line
        {
            X1 = ax, X2 = ax, Y1 = PlotTop, Y2 = PlotTop + plotHeight, Stroke = Named("AxisBrush"),
            StrokeThickness = 1, StrokeDashArray = [3, 3],
        });

        var rows = new StackPanel { Spacing = 2 };
        rows.Children.Add(new TextBlock
        {
            Text = Fmt.Mv(anchor), FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Named("TextBrush"),
        });
        foreach (var (s, p) in hits)
        {
            var ring = new Ellipse
            {
                Width = 10, Height = 10, Stroke = BrushFor(s.Color), StrokeThickness = 2, Fill = Named("TipBackgroundBrush"),
            };
            Canvas.SetLeft(ring, MapX(p.X) - 5);
            Canvas.SetTop(ring, MapY(p.Y) - 5);
            hover.Children.Add(ring);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = BrushFor(s.Color), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock
            {
                Text = $"{s.Name}: {Fmt.Mhz(p.Y)}", FontSize = 12, Foreground = Named("TextBrush"),
            });
            rows.Children.Add(row);
        }

        var tip = new Border
        {
            Child = rows, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6),
            Background = Named("TipBackgroundBrush"), BorderBrush = Named("TipBorderBrush"), BorderThickness = new Thickness(1),
        };
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tx = ax + 14;
        if (tx + tip.DesiredSize.Width > PlotLeft + plotWidth) tx = ax - 14 - tip.DesiredSize.Width;
        var ty = Math.Clamp(pos.Y - tip.DesiredSize.Height / 2, PlotTop, Math.Max(PlotTop, PlotTop + plotHeight - tip.DesiredSize.Height));
        Canvas.SetLeft(tip, Math.Max(PlotLeft, tx));
        Canvas.SetTop(tip, ty);
        hover.Children.Add(tip);
    }
}
