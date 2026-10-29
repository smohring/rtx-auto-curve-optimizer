using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using RtxAutoCurveOptimizer.Afterburner;
using RtxAutoCurveOptimizer.App.Controls;
using RtxAutoCurveOptimizer.App.Services;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization;

namespace RtxAutoCurveOptimizer.App.ViewModels;

/// <summary>A selectable preset with its estimated duration.</summary>
public sealed record PresetOption(OptimizationPreset Preset, string Title, string Detail)
{
    /// <summary>Accessible name of the radio button.</summary>
    public override string ToString() => $"{Title}, {Detail}";
}

/// <summary>One line of the event log.</summary>
public sealed record LogEntry(string Time, string Text)
{
    /// <summary>Accessible name of the list item.</summary>
    public override string ToString() => $"{Time} {Text}";
}

/// <summary>An editable anchor voltage in the anchor list.</summary>
public sealed partial class AnchorItem : ObservableObject
{
    private readonly Action<AnchorItem> remove;
    private readonly Action changed;

    public AnchorItem(double voltageMv, Action<AnchorItem> remove, Action changed)
    {
        this.remove = remove;
        this.changed = changed;
        VoltageMv = voltageMv;
    }

    [ObservableProperty]
    public partial double VoltageMv { get; set; }

    partial void OnVoltageMvChanged(double value) => changed();

    /// <summary>Removes this anchor from the list.</summary>
    public void Remove() => remove(this);
}

/// <summary>Optimize page: settings, run control, live progress, live chart, telemetry and event log.</summary>
public sealed partial class OptimizeViewModel : ObservableObject
{
    private const int MaxLogLines = 400;

    private readonly GpuSession session;
    private readonly HeartbeatWriter heartbeat;
    private readonly Lock pendingGate = new();
    private DispatcherQueueTimer? uiTimer;
    private Telemetry? pendingTelemetry;
    private StressProgress? pendingStress;
    private OptimizerSettings activeSettings = OptimizerSettings.For(OptimizationPreset.Quick);
    private StockCurve? stock;
    private CurveOffsets? currentOffsets;
    private int[] anchorPoints = [];
    private TimeSpan etaTotal;
    private TimeSpan testDuration;
    private readonly List<ChartPoint> passed = [], failed = [], maxStable = [];
    private ChartPoint? testing;
    private bool suppressAnchorEvents;

    public OptimizeViewModel(GpuSession session)
    {
        this.session = session;
        heartbeat = new HeartbeatWriter(session.Options);
        Tiles = new TelemetryTiles();
        Presets =
        [
            MakePreset(OptimizationPreset.Quick), MakePreset(OptimizationPreset.Standard), MakePreset(OptimizationPreset.Thorough),
        ];
        PhaseText = Loc.Get("Phase_Idle");
        EtaText = string.Empty;
        TestText = string.Empty;
        TestProgressText = string.Empty;
        ResultTitle = string.Empty;
        ResultMessage = string.Empty;
        EstimateText = string.Empty;
        ResultSeverity = InfoBarSeverity.Informational;
        ChartXMin = double.NaN;
        ChartXMax = double.NaN;
        NewAnchorMv = 975;
        SelectedPreset = Presets[0];
        LoadPresetAnchors();
        SafetyMarginMhz = OptimizerSettings.For(OptimizationPreset.Quick).SafetyMarginMhz;
        session.SessionChanged += OnSessionChanged;
        session.BusyChanged += RefreshState;
        session.OffsetsChanged += ReadCurrentOffsets;
        OnSessionChanged();
    }

    public IReadOnlyList<PresetOption> Presets { get; }

    public ObservableCollection<AnchorItem> Anchors { get; } = [];

    public ObservableCollection<LogEntry> Log { get; } = [];

    public TelemetryTiles Tiles { get; }

    /// <summary>Index into <see cref="Presets"/>; RadioButtons may push -1 transiently while switching.</summary>
    [ObservableProperty]
    public partial int SelectedPresetIndex { get; set; }

    private PresetOption SelectedPreset { get; set; } = null!;

    [ObservableProperty]
    public partial double SafetyMarginMhz { get; set; }

    [ObservableProperty]
    public partial double NewAnchorMv { get; set; }

    [ObservableProperty]
    public partial string EstimateText { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool NotElevated { get; set; }

    [ObservableProperty]
    public partial bool NoGpu { get; set; }

    [ObservableProperty]
    public partial string PhaseText { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; }

    [ObservableProperty]
    public partial string EtaText { get; set; }

    [ObservableProperty]
    public partial string TestText { get; set; }

    [ObservableProperty]
    public partial string TestProgressText { get; set; }

    [ObservableProperty]
    public partial bool IsResultOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity ResultSeverity { get; set; }

    [ObservableProperty]
    public partial string ResultTitle { get; set; }

    [ObservableProperty]
    public partial string ResultMessage { get; set; }

    [ObservableProperty]
    public partial bool HasResultAction { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ChartSeries>? Series { get; set; }

    [ObservableProperty]
    public partial double ChartXMin { get; set; }

    [ObservableProperty]
    public partial double ChartXMax { get; set; }

    public bool CanEdit => !IsRunning && !session.IsBusy;

    private PresetOption MakePreset(OptimizationPreset preset)
    {
        var estimate = OptimizerSettings.For(preset).EstimateDuration();
        return new PresetOption(preset, Loc.Get("Preset_" + preset), Loc.Format("Preset_Estimate", Fmt.Duration(estimate)));
    }

    partial void OnSelectedPresetIndexChanged(int value)
    {
        if (value < 0 || value >= Presets.Count) return;
        SelectedPreset = Presets[value];
        LoadPresetAnchors();
        SafetyMarginMhz = OptimizerSettings.For(SelectedPreset.Preset).SafetyMarginMhz;
    }

    partial void OnSafetyMarginMhzChanged(double value) => UpdateEstimate();

    partial void OnIsRunningChanged(bool value) => RefreshState();

    private void LoadPresetAnchors()
    {
        suppressAnchorEvents = true;
        Anchors.Clear();
        foreach (var mv in OptimizerSettings.For(SelectedPreset.Preset).AnchorVoltagesMv)
            Anchors.Add(new AnchorItem(mv, RemoveAnchor, UpdateEstimate));
        suppressAnchorEvents = false;
        UpdateEstimate();
    }

    /// <summary>Shows the preset, anchors and margin of a resumed session in the editor.</summary>
    private void ShowSettings(OptimizerSettings settings)
    {
        for (var i = 0; i < Presets.Count; i++)
        {
            if (OptimizerSettings.For(Presets[i].Preset).AnchorVoltagesMv.SequenceEqual(settings.AnchorVoltagesMv))
                SelectedPresetIndex = i;
        }
        suppressAnchorEvents = true;
        Anchors.Clear();
        foreach (var mv in settings.AnchorVoltagesMv) Anchors.Add(new AnchorItem(mv, RemoveAnchor, UpdateEstimate));
        suppressAnchorEvents = false;
        SafetyMarginMhz = settings.SafetyMarginMhz;
        UpdateEstimate();
    }

    private void RemoveAnchor(AnchorItem item)
    {
        Anchors.Remove(item);
        UpdateEstimate();
    }

    private void UpdateEstimate()
    {
        if (suppressAnchorEvents) return;
        var settings = BuildSettings();
        EstimateText = Loc.Format("Optimize_Estimate", Fmt.Duration(settings.EstimateDuration()), settings.AnchorVoltagesMv.Count);
        RefreshState();
    }

    /// <summary>Resets the anchors to the selected preset.</summary>
    [RelayCommand]
    private void ResetAnchors()
    {
        LoadPresetAnchors();
        SafetyMarginMhz = OptimizerSettings.For(SelectedPreset.Preset).SafetyMarginMhz;
    }

    /// <summary>Adds the voltage from the input box to the sorted anchor list.</summary>
    [RelayCommand]
    private void AddAnchor()
    {
        if (double.IsNaN(NewAnchorMv)) return;
        var mv = Math.Round(NewAnchorMv);
        if (Anchors.Any(a => Math.Abs(a.VoltageMv - mv) < 0.5)) return;
        var index = 0;
        while (index < Anchors.Count && Anchors[index].VoltageMv < mv) index++;
        Anchors.Insert(index, new AnchorItem(mv, RemoveAnchor, UpdateEstimate));
        UpdateEstimate();
    }

    private OptimizerSettings BuildSettings()
    {
        var anchors = Anchors.Where(a => !double.IsNaN(a.VoltageMv))
            .Select(a => (int)Math.Round(a.VoltageMv)).Distinct().Order().ToList();
        var margin = double.IsNaN(SafetyMarginMhz) ? 30 : (int)Math.Round(SafetyMarginMhz);
        return OptimizerSettings.For(SelectedPreset.Preset) with { AnchorVoltagesMv = anchors, SafetyMarginMhz = (int)margin };
    }

    private void OnSessionChanged()
    {
        stock = session.Stock;
        ResetRunState();
        ReadCurrentOffsets();
        RefreshState();
    }

    /// <summary>Reads the offsets currently on the GPU for the chart's "current" line.</summary>
    /// <summary>The worker's driver resets leave the app's own handles stale; reopen them before the app reads the GPU again.</summary>
    private void ReopenGpu()
    {
        if (session.DemoMode) return;
        try
        {
            session.Gpu?.Reinitialize();
        }
        catch (GpuControlException)
        {
        }
    }

    private void ReadCurrentOffsets()
    {
        currentOffsets = null;
        try
        {
            currentOffsets = session.Gpu?.ReadOffsets();
        }
        catch (GpuControlException)
        {
        }

        BuildChart();
    }

    private void RefreshState()
    {
        NoGpu = session.Gpu is null;
        NotElevated = session.Gpu is { CanWrite: false };
        OnPropertyChanged(nameof(CanEdit));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() =>
        !IsRunning && !session.IsBusy && session.Gpu is { CanWrite: true } && BuildSettings().AnchorVoltagesMv.Count > 0;

    private bool CanCancel() => IsRunning;

    /// <summary>Asks about a running Afterburner, then starts a full optimization.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!await ConfirmAfterburnerClosedAsync()) return;
        var settings = BuildSettings();
        PrepareRun(settings, null);
        await RunAsync((pump, ct) => session.DemoMode
            ? session.GetOptimizer().RunAsync(settings, pump, ct)
            : WorkerClient.RunAsync(new WorkerRequest(settings, null), session.Gpu!, session.Store, pump, ct));
    }

    /// <summary>Continues a recovered session with the stored settings and results.</summary>
    public async Task ResumeAsync(SessionState recovered)
    {
        if (!CanStart()) return;
        ShowSettings(recovered.Settings);
        PrepareRun(recovered.Settings, recovered);
        await RunAsync((pump, ct) => session.DemoMode
            ? session.GetOptimizer().ResumeAsync(recovered, pump, ct)
            : WorkerClient.RunAsync(new WorkerRequest(null, recovered.Id), session.Gpu!, session.Store, pump, ct));
    }

    /// <summary>Cancels the run; the optimizer restores the original offsets before it returns.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        session.CancelOperation();
        PhaseText = Loc.Get("Phase_Cancelling");
        IsIndeterminate = true;
    }

    /// <summary>Asks to close a running Afterburner; not needed in demo mode, which never touches the GPU.</summary>
    private async Task<bool> ConfirmAfterburnerClosedAsync()
    {
        if (session.DemoMode) return true;
        while (await Task.Run(AfterburnerProcess.IsRunning))
        {
            var answer = await Dialogs.ShowAsync(
                Loc.Get("Dialog_AfterburnerTitle"), Loc.Get("Dialog_AfterburnerMessage"),
                Loc.Get("Dialog_AfterburnerRetry"), Loc.Get("Dialog_AfterburnerIgnore"), Loc.Get("Dialog_Cancel"));
            if (answer == ContentDialogResult.None) return false;
            if (answer == ContentDialogResult.Secondary) return true;
        }

        return true;
    }

    private void PrepareRun(OptimizerSettings settings, SessionState? resumed)
    {
        activeSettings = settings;
        stock = session.Stock;
        ResetRunState();
        if (stock is not null)
            anchorPoints = [.. settings.AnchorVoltagesMv.Select(mv => CurveBuilder.NearestPointIndex(stock, mv)).Distinct().Order()];

        if (resumed is not null)
        {
            foreach (var t in resumed.Tests) AddTestPoint(t);
            foreach (var a in resumed.Anchors) maxStable.Add(new ChartPoint(a.VoltageUv / 1000.0, a.MaxStableKhz / 1000.0));
        }

        IsResultOpen = false;
        PhaseText = Loc.Get("Phase_Preparing");
        IsIndeterminate = true;
        Progress = 0;
        BuildChart();
    }

    private void ResetRunState()
    {
        passed.Clear();
        failed.Clear();
        maxStable.Clear();
        testing = null;
        etaTotal = TimeSpan.Zero;
        TestText = string.Empty;
        TestProgressText = string.Empty;
        EtaText = string.Empty;
        Progress = 0;
        IsIndeterminate = false;
        PhaseText = Loc.Get("Phase_Idle");
        lock (pendingGate)
        {
            pendingTelemetry = null;
            pendingStress = null;
        }

        Tiles.Clear();
    }

    /// <summary>Runs an optimization (in the worker process on real hardware) and shows its events and result.</summary>
    private async Task RunAsync(Func<IProgress<OptimizerEvent>, CancellationToken, Task<OptimizationResult>> runner)
    {
        IsRunning = true;
        Log.Clear();
        AddLog(Loc.Format("Log_Started", activeSettings.AnchorVoltagesMv.Count));
        if (session.Options.Restarts > 0) AddLog(Loc.Format("Log_WatchdogRestart", session.Options.Restarts, WatchdogPolicy.MaxRestarts));
        StartUiTimer();
        var pump = new Pump(this);
        heartbeat.Start();
        try
        {
            var result = await session.RunExclusiveAsync(ct => Task.Run(() => runner(pump, ct), CancellationToken.None));
            ApplyPending();
            Finish(result);
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, Loc.Get("Result_ErrorTitle"), ex.Message, false);
            AddLog(Loc.Format("Log_Error", ex.Message));
            PhaseText = Loc.Get("Phase_Idle");
        }
        finally
        {
            heartbeat.Finish();
            ReopenGpu();
            StopUiTimer();
            IsRunning = false;
            IsIndeterminate = false;
            testing = null;
            ReadCurrentOffsets();
        }
    }

    private void Finish(OptimizationResult result)
    {
        if (result.Anchors.Count > 0 || result.Proposals.Count > 0) session.SetResult(result);
        PhaseText = Loc.Get("Phase_Idle");
        EtaText = string.Empty;
        TestText = string.Empty;
        TestProgressText = string.Empty;
        AddLog(Loc.Format("Log_Finished", Loc.Get("Status_" + result.Status), Fmt.Duration(result.Elapsed)));

        var restoreNote = result.OffsetsRestored ? string.Empty : " " + Loc.Get("Result_NotRestored");
        switch (result.Status)
        {
            case OptimizationStatus.Completed:
                Progress = 100;
                var recommended = result.Recommended;
                var message = recommended is null
                    ? Loc.Get("Result_CompletedNoProposal")
                    : Loc.Format("Result_Completed", Fmt.UvAsMv(recommended.CapAnchor.VoltageUv),
                        Fmt.KhzAsMhz(recommended.CapAnchor.RecommendedKhz), Fmt.Percent(recommended.ExpectedRelativePerformance),
                        Fmt.Watts(recommended.ExpectedPowerW));
                ShowResult(result.OffsetsRestored ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                    Loc.Get("Result_CompletedTitle"), message + restoreNote, true);
                break;
            case OptimizationStatus.Cancelled:
                ShowResult(result.OffsetsRestored ? InfoBarSeverity.Informational : InfoBarSeverity.Warning,
                    Loc.Get("Result_CancelledTitle"),
                    Loc.Get(result.OffsetsRestored ? "Result_Cancelled" : "Result_NotRestored"), result.Anchors.Count > 0);
                break;
            default:
                var reason = Loc.Get("Failure_" + result.Failure);
                var detail = string.IsNullOrEmpty(result.FailureDetail) ? string.Empty : $" ({result.FailureDetail})";
                ShowResult(InfoBarSeverity.Error, Loc.Get("Result_FailedTitle"), reason + detail + restoreNote, result.Anchors.Count > 0);
                break;
        }
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message, bool withAction)
    {
        ResultSeverity = severity;
        ResultTitle = title;
        ResultMessage = message;
        HasResultAction = withAction;
        IsResultOpen = true;
    }

    private void StartUiTimer()
    {
        uiTimer = session.Dispatcher.CreateTimer();
        uiTimer.Interval = TimeSpan.FromMilliseconds(250);
        uiTimer.Tick += (_, _) => ApplyPending();
        uiTimer.Start();
    }

    private void StopUiTimer()
    {
        uiTimer?.Stop();
        uiTimer = null;
    }

    /// <summary>Shows the newest telemetry and test progress received since the last tick and refreshes the chart.</summary>
    private void ApplyPending()
    {
        Telemetry? telemetry;
        StressProgress? stress;
        lock (pendingGate)
        {
            (telemetry, pendingTelemetry) = (pendingTelemetry, null);
            (stress, pendingStress) = (pendingStress, null);
        }

        if (telemetry is { } t) Tiles.Update(t);
        if (stress is { } s && testDuration > TimeSpan.Zero)
            TestProgressText = Loc.Format("Optimize_TestProgress", Fmt.Duration(s.Elapsed), Fmt.Duration(testDuration));
        BuildChart();
    }

    private void Handle(OptimizerEvent e)
    {
        switch (e)
        {
            case PhaseChanged p:
                PhaseText = Loc.Get("Phase_" + p.Phase);
                IsIndeterminate = p.Phase is OptimizerPhase.Preparing or OptimizerPhase.Restoring && etaTotal == TimeSpan.Zero;
                if (p.Phase != OptimizerPhase.Finished) AddLog(Loc.Format("Log_Phase", PhaseText));
                break;
            case TestStarted t:
                OnTestStarted(t);
                break;
            case TestFinished f:
                OnTestFinished(f.Record);
                break;
            case AnchorCompleted a:
                maxStable.Add(new ChartPoint(a.Result.VoltageUv / 1000.0, a.Result.MaxStableKhz / 1000.0));
                AddLog(Loc.Format("Log_Anchor", Fmt.UvAsMv(a.Result.VoltageUv), Fmt.KhzAsMhz(a.Result.MaxStableKhz),
                    Fmt.KhzAsMhz(a.Result.RecommendedKhz), Fmt.Percent(a.Result.ThroughputRelative)));
                break;
            case EtaUpdated eta:
                if (eta.Remaining > etaTotal) etaTotal = eta.Remaining;
                IsIndeterminate = false;
                Progress = etaTotal > TimeSpan.Zero ? 100 * (1 - eta.Remaining / etaTotal) : 0;
                EtaText = Loc.Format("Optimize_EtaRemaining", Fmt.Duration(eta.Remaining));
                break;
            case Warning w:
                AddLog(WarningText(w));
                break;
        }
    }

    private void OnTestStarted(TestStarted t)
    {
        testDuration = t.Duration;
        var point = PointOf(t.AnchorIndex);
        var testLabel = Loc.Get("Kind_" + t.Kind);
        if (point is { } p && stock is not null && t.Kind is TestKind.Search or TestKind.Confirm or TestKind.Validation)
        {
            var mv = stock.Points[p].VoltageUv / 1000.0;
            testing = new ChartPoint(mv, t.FrequencyKhz / 1000.0);
            TestText = Loc.Format("Optimize_TestAt", testLabel, Fmt.Mv(mv), Fmt.KhzAsMhz(t.FrequencyKhz));
        }
        else
        {
            testing = null;
            TestText = testLabel;
        }

        TestProgressText = Loc.Format("Optimize_TestProgress", Fmt.Duration(TimeSpan.Zero), Fmt.Duration(t.Duration));
    }

    private void OnTestFinished(TestRecord r)
    {
        testing = null;
        AddTestPoint(r);
        var what = Loc.Get("Kind_" + r.Kind);
        var outcome = Loc.Get("Outcome_" + (r.Outcome == StressOutcome.Passed && !r.Valid ? "Invalid" : r.Outcome.ToString()));
        if (r.PointIndex >= 0 && stock is not null)
            AddLog(Loc.Format("Log_TestAt", r.Sequence, what, Fmt.UvAsMv(stock.Points[r.PointIndex].VoltageUv),
                Fmt.KhzAsMhz(r.FrequencyKhz), outcome));
        else
            AddLog(Loc.Format("Log_Test", r.Sequence, what, outcome));
    }

    private void AddTestPoint(TestRecord r)
    {
        if (r.PointIndex < 0 || stock is null || r.PointIndex >= stock.Points.Count) return;
        if (r.Kind is not (TestKind.Search or TestKind.Confirm or TestKind.Validation)) return;
        var point = new ChartPoint(stock.Points[r.PointIndex].VoltageUv / 1000.0, r.FrequencyKhz / 1000.0);
        (r.Passed ? passed : failed).Add(point);
    }

    private int? PointOf(int anchorIndex) =>
        anchorIndex >= 0 && anchorIndex < anchorPoints.Length ? anchorPoints[anchorIndex] : null;

    private string WarningText(Warning w)
    {
        var detail = w.Detail ?? string.Empty;
        if (w.Kind is WarningKind.AnchorSkipped or WarningKind.AnchorLimited or WarningKind.AnchorUnconfirmed or WarningKind.CoolingDown
            && int.TryParse(detail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && PointOf(index) is { } point && stock is not null)
            detail = Fmt.UvAsMv(stock.Points[point].VoltageUv);
        return Loc.Format("Warning_" + w.Kind, detail);
    }

    private void AddLog(string text)
    {
        Log.Add(new LogEntry(DateTime.Now.ToString("T", CultureInfo.CurrentCulture), text));
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

    private void BuildChart()
    {
        if (stock is null)
        {
            Series = null;
            return;
        }

        (ChartXMin, ChartXMax) = ChartSeries.VoltageRange(stock);
        List<ChartSeries> series =
        [
            new() { Name = Loc.Get("Chart_Stock"), Color = ChartColor.Stock, Points = ChartSeries.CurvePoints(stock, null) },
        ];
        if (currentOffsets is not null)
            series.Add(new ChartSeries
            {
                Name = Loc.Get("Chart_Current"), Color = ChartColor.Current, Dashed = true, Thickness = 1.5,
                Points = ChartSeries.CurvePoints(stock, currentOffsets),
            });
        series.Add(new ChartSeries
        {
            Name = Loc.Get("Chart_MaxStable"), Color = ChartColor.Accent, MarkerSize = 7, Points = [.. maxStable],
        });
        series.Add(new ChartSeries
        {
            Name = Loc.Get("Chart_Passed"), Color = ChartColor.Success, ShowLine = false, MarkerSize = 7, Points = [.. passed],
        });
        series.Add(new ChartSeries
        {
            Name = Loc.Get("Chart_Failed"), Color = ChartColor.Critical, ShowLine = false, MarkerSize = 7, Points = [.. failed],
        });
        if (testing is { } t)
            series.Add(new ChartSeries
            {
                Name = Loc.Get("Chart_Testing"), Color = ChartColor.Caution, ShowLine = false, MarkerSize = 12, Points = [t],
            });
        Series = series;
    }

    /// <summary>Receives optimizer events on any thread: telemetry is coalesced, everything else goes to the UI thread in order.</summary>
    private sealed class Pump(OptimizeViewModel vm) : IProgress<OptimizerEvent>
    {
        public void Report(OptimizerEvent value)
        {
            switch (value)
            {
                case TelemetrySampled t:
                    lock (vm.pendingGate) vm.pendingTelemetry = t.Telemetry;
                    break;
                case StressProgressed s:
                    lock (vm.pendingGate) vm.pendingStress = s.Progress;
                    break;
                default:
                    vm.session.Dispatcher.TryEnqueue(() => vm.Handle(value));
                    break;
            }
        }
    }
}
