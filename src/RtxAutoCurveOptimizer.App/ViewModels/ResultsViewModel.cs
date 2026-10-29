using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using RtxAutoCurveOptimizer.Afterburner;
using RtxAutoCurveOptimizer.App.Controls;
using RtxAutoCurveOptimizer.App.Services;
using RtxAutoCurveOptimizer.App.Views;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization;

namespace RtxAutoCurveOptimizer.App.ViewModels;

/// <summary>One row of the anchor table (all values preformatted for the current culture).</summary>
public sealed record AnchorRow(
    string Voltage, string Stock, string MaxStable, string Recommended, string Power, string Performance,
    string PerfPerWatt, string Note);

/// <summary>A selectable card for one proposal, or for several identical proposals merged into one.</summary>
public sealed record ProposalCard(
    ProposalGroup Group, string Title, bool IsRecommended, string CapText, string PowerText, string PowerDeltaText,
    string PerformanceText, string MarginText, bool IsValidated, string ValidationText)
{
    /// <summary>The proposal that stands for the card (offsets, cap anchor, kind).</summary>
    public ProfileProposal Proposal => Group.Primary;

    public bool IsPending => !IsValidated;

    /// <summary>Accessible name of the grid item.</summary>
    public override string ToString() => $"{Title}, {CapText}, {PowerText}, {PerformanceText}, {ValidationText}";
}

/// <summary>Results page: anchor table, proposals, chart and the apply / validate / save / export / restore actions.</summary>
public sealed partial class ResultsViewModel : ObservableObject
{
    private readonly GpuSession session;
    private CurveOffsets? currentOffsets;
    private OptimizationResult? result;

    public ResultsViewModel(GpuSession session)
    {
        this.session = session;
        SummaryText = string.Empty;
        StatusTitle = string.Empty;
        StatusMessage = string.Empty;
        OperationText = string.Empty;
        ChartXMin = double.NaN;
        ChartXMax = double.NaN;
        StatusSeverity = InfoBarSeverity.Informational;
        session.ResultChanged += Reload;
        session.SessionChanged += () => currentOffsets = null;
        session.BusyChanged += RefreshState;
        session.OffsetsChanged += () => _ = RefreshCurrentAsync();
        Reload();
    }

    public ObservableCollection<AnchorRow> Anchors { get; } = [];

    public ObservableCollection<ProposalCard> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasResult { get; set; }

    public bool IsEmpty => !HasResult;

    [ObservableProperty]
    public partial string SummaryText { get; set; }

    [ObservableProperty]
    public partial ProposalCard? SelectedCard { get; set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; }

    [ObservableProperty]
    public partial string StatusTitle { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool IsValidating { get; set; }

    [ObservableProperty]
    public partial string OperationText { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ChartSeries>? Series { get; set; }

    [ObservableProperty]
    public partial double ChartXMin { get; set; }

    [ObservableProperty]
    public partial double ChartXMax { get; set; }

    [ObservableProperty]
    public partial bool NotElevated { get; set; }

    /// <summary>MHz the selected curve is shifted by in 15 MHz steps: positive raises (up to max stable), negative lowers.</summary>
    [ObservableProperty]
    public partial double ShiftMhz { get; set; }

    /// <summary>Highest allowed shift of the selected proposal: up to the clocks set during the tests.</summary>
    [ObservableProperty]
    public partial double MaxShiftMhz { get; set; }

    /// <summary>"+60 MHz" for the shift slider.</summary>
    [ObservableProperty]
    public partial string ShiftText { get; set; } = string.Empty;

    /// <summary>Top clock of the selected curve: lower cuts the curve off, higher moves the cap to a higher anchor.</summary>
    [ObservableProperty]
    public partial double ClockLimitMhz { get; set; }

    /// <summary>Highest selectable top clock (see <see cref="ProposalBuilder.MaxLimitMhz"/>); the slider's maximum.</summary>
    [ObservableProperty]
    public partial double PeakClockMhz { get; set; }

    /// <summary>Lowest selectable clock limit: the first anchor's recommended clock.</summary>
    [ObservableProperty]
    public partial double MinClockMhz { get; set; }

    [ObservableProperty]
    public partial string ClockLimitText { get; set; } = string.Empty;

    /// <summary>Cap, power and performance of the curve as currently shifted and limited.</summary>
    [ObservableProperty]
    public partial string SelectionText { get; set; } = string.Empty;

    /// <summary>Offsets of Afterburner's startup profile, the user's proven daily curve; limits the untested range below the first anchor.</summary>
    private CurveOffsets? provenBase;

    /// <summary>The selected proposal, shifted and set to <see cref="ClockLimitMhz"/>; what Apply, Save, Export and Validate use.</summary>
    private ProfileProposal? Selected
    {
        get
        {
            if (SelectedCard is not { } card || result is null) return null;
            var limit = (int)Math.Round(Math.Clamp(ClockLimitMhz, MinClockMhz, PeakClockMhz));
            var p = card.Proposal;
            if (p.Kind == ProposalKind.Uniform)
                return ProposalBuilder.Uniform(result.Stock, result.Anchors, StockBoostMhz, ShiftStep, limit);
            if (!IsRaw(p))
                return ProposalBuilder.Limited(result.Stock, result.Anchors, p, ShiftStep, limit, provenBase ?? result.OriginalOffsets);
            var stock = result.Stock.Points;
            return p with
            {
                Offsets = new CurveOffsets([.. p.Offsets.OffsetsKhz.Select((o, j) =>
                    Math.Min(Math.Clamp(o + ShiftStep * 1000, -CurveBuilder.MaxOffsetKhz, CurveBuilder.MaxOffsetKhz), limit * 1000 - stock[j].StockKhz))]),
            };
        }
    }

    /// <summary>Stock boost clock under load from the run's stock reference; the default cap of the uniform offset.</summary>
    private double StockBoostMhz => result?.StockReference?.Averages.AvgClockMhz ?? 0;

    /// <summary><see cref="ShiftMhz"/> in 15 MHz steps within its allowed range.</summary>
    private int ShiftStep => double.IsNaN(ShiftMhz) ? 0 : Math.Clamp((int)(Math.Round(ShiftMhz / 15) * 15), -150, (int)MaxShiftMhz);

    partial void OnShiftMhzChanged(double value)
    {
        UpdateClockRange();
        BuildChart();
    }

    partial void OnClockLimitMhzChanged(double value)
    {
        ClockLimitText = Fmt.Mhz(value);
        BuildChart();
    }

    /// <summary>Sets the clock limit range for the shifted curve and resets the limit to that curve's own top clock.</summary>
    private void UpdateClockRange()
    {
        ShiftText = ShiftStep == 0 ? "0 MHz" : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:+0;-0} MHz", ShiftStep);
        if (SelectedCard is not { } card || result is null)
        {
            PeakClockMhz = MinClockMhz = ClockLimitMhz = 0;
            return;
        }

        var p = card.Proposal;
        var below = provenBase ?? result.OriginalOffsets;
        var own = p.Kind == ProposalKind.Uniform
            ? ProposalBuilder.PeakMhz(result.Stock, ProposalBuilder.Uniform(result.Stock, result.Anchors, StockBoostMhz, ShiftStep))
            : IsRaw(p)
            ? Peak(result.Stock, p.Offsets).Khz / 1000 + ShiftStep
            : ProposalBuilder.PeakMhz(result.Stock, ProposalBuilder.Shifted(result.Stock, result.Anchors, p, ShiftStep, below));
        PeakClockMhz = p.Kind == ProposalKind.Uniform
            ? Math.Max(own, ProposalBuilder.UniformMaxCapMhz(result.Stock, result.Anchors, StockBoostMhz, ShiftStep))
            : IsRaw(p) ? own : ProposalBuilder.MaxLimitMhz(result.Stock, result.Anchors, p, ShiftStep, below);
        MinClockMhz = Math.Min(own, result.Anchors.Min(a => a.RecommendedKhz) / 15_000 * 15);
        ClockLimitMhz = own;
        OnClockLimitMhzChanged(own);
    }

    partial void OnSelectedCardChanged(ProposalCard? value)
    {
        MaxShiftMhz = value is null || result is null ? 0
            : IsRaw(value.Proposal) ? CurveBuilder.MaxRaiseMhz(result.Stock, result.Anchors, value.Proposal.Offsets)
            : CurveBuilder.MaxRaiseMhz(result.Anchors, value.Proposal.CapAnchor) + value.Proposal.ExtraMarginMhz;
        ShiftMhz = 0;
        UpdateClockRange();
        BuildAnchorRows();
        BuildChart();
        RefreshState();
    }

    /// <summary>Rebuilds table, cards and chart from the session's last result, keeping the selected proposal kind.</summary>
    private void Reload()
    {
        var keep = SelectedCard?.Proposal.Kind;
        result = session.LastResult;
        HasResult = result is { Anchors.Count: > 0 };
        Cards.Clear();
        if (result is null || !HasResult)
        {
            Anchors.Clear();
            SelectedCard = null;
            SummaryText = string.Empty;
            BuildChart();
            RefreshState();
            return;
        }

        provenBase = ReadStartupProfile(result.Stock);
        var stockPowerW = result.StockReference?.Averages.AvgPowerW ?? 0;
        foreach (var group in ProposalGroup.Merge(result.Proposals)) Cards.Add(MakeCard(result.Stock, group, stockPowerW));
        if (result.StockReference is { } reference)
            Cards.Add(MakeCard(result.Stock, new ProposalGroup([ProposalBuilder.Uniform(result.Stock, result.Anchors,
                reference.Averages.AvgClockMhz)]), stockPowerW));
        if (currentOffsets is { } current && current.OffsetsKhz.Count == result.Stock.Points.Count)
            Cards.Add(MakeCurrentCard(result, current));
        SelectedCard = (keep is { } kind ? Cards.FirstOrDefault(c => c.Group.Contains(kind)) : null)
                       ?? Cards.FirstOrDefault(c => c.IsRecommended) ?? Cards.FirstOrDefault();
        BuildAnchorRows();
        BuildSummary(result);
        BuildChart();
        RefreshState();
    }

    private CurveOffsets? ReadStartupProfile(StockCurve stock)
    {
        try
        {
            return session.Gpu is { } gpu && session.ProfileStore.FindProfileFile(gpu.Identity) is { } path
                ? session.ProfileStore.Read(path).GetCurve("Startup")?.ToOffsets(stock)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The curve currently on the GPU (e.g. loaded from Afterburner) as a card that can be shifted, applied and saved.</summary>
    private static ProposalCard MakeCurrentCard(OptimizationResult r, CurveOffsets current)
    {
        var (voltageUv, maxKhz) = Peak(r.Stock, current);
        var proposal = new ProfileProposal(ProposalKind.Custom, r.Anchors.MaxBy(a => a.VoltageUv)!, current, 0, 0, 0, false);
        return new ProposalCard(
            new ProposalGroup([proposal]), Loc.Get("Proposal_Custom"), false,
            Loc.Format("Results_CapText", Fmt.UvAsMv(voltageUv), Fmt.KhzAsMhz(maxKhz)),
            TileViewModel.Placeholder, string.Empty, TileViewModel.Placeholder, string.Empty,
            false, Loc.Get("Results_NotValidated"));
    }

    /// <summary>Highest frequency of a curve and the lowest voltage that reaches it.</summary>
    private static (int VoltageUv, int Khz) Peak(StockCurve stock, CurveOffsets offsets)
    {
        var points = stock.Points.Select((p, i) => (p.VoltageUv, Khz: p.StockKhz + offsets.OffsetsKhz[i])).ToList();
        var max = points.Max(p => p.Khz);
        return points.First(p => p.Khz == max);
    }

    private static ProposalCard MakeCard(StockCurve stock, ProposalGroup group, double stockPowerW)
    {
        var p = group.Primary;
        var (capUv, capKhz) = Peak(stock, p.Offsets);
        var margin = p.ExtraMarginMhz > 0 ? Loc.Format("Results_MarginText", p.ExtraMarginMhz) : string.Empty;
        var title = string.Join(" · ", group.Members.Select(m => Loc.Get("Proposal_" + m.Kind)));
        return new ProposalCard(
            group, title, group.IsRecommended,
            Loc.Format("Results_CapText", Fmt.UvAsMv(capUv), Fmt.KhzAsMhz(capKhz)),
            Fmt.Watts(p.ExpectedPowerW),
            stockPowerW > 0 ? Loc.Format("Results_PowerVsStock", Fmt.SignedWatts(p.ExpectedPowerW - stockPowerW)) : string.Empty,
            Fmt.Percent(p.ExpectedRelativePerformance), margin,
            group.Validated, Loc.Get(group.Validated ? "Results_Validated" : "Results_NotValidated"));
    }

    private void BuildSummary(OptimizationResult r)
    {
        var text = Loc.Format("Results_Summary", Loc.Get("Status_" + r.Status), Fmt.Duration(r.Elapsed), r.Tests.Count);
        if (r.StockReference is { } s)
            text += " " + Loc.Format("Results_Reference", Fmt.Watts(s.Averages.AvgPowerW), Fmt.Mhz(s.Averages.AvgClockMhz));
        SummaryText = text;
    }

    private void BuildAnchorRows()
    {
        Anchors.Clear();
        if (result is null) return;
        var reference = result.StockReference;
        var stockPerWatt = reference is { Averages.AvgPowerW: > 0 } ? reference.Throughput / reference.Averages.AvgPowerW : 0;
        var cap = SelectedCard?.Proposal.CapAnchor.AnchorIndex;
        foreach (var a in result.Anchors.OrderBy(a => a.VoltageUv))
        {
            var notes = new List<string>();
            if (a.AnchorIndex == cap) notes.Add(Loc.Get("Results_NoteCap"));
            if (a.Limited) notes.Add(Loc.Get("Results_NoteLimited"));
            Anchors.Add(new AnchorRow(
                Fmt.UvAsMv(a.VoltageUv), Fmt.KhzAsMhz(a.StockKhz), Fmt.KhzAsMhz(a.MaxStableKhz), Fmt.KhzAsMhz(a.RecommendedKhz),
                Fmt.Watts(a.AvgPowerW), Fmt.Percent(a.ThroughputRelative),
                stockPerWatt > 0 ? Fmt.Percent(a.PerfPerWatt / stockPerWatt) : TileViewModel.Placeholder,
                string.Join(", ", notes)));
        }
    }

    private void BuildChart()
    {
        if (result is null || SelectedCard is null)
        {
            Series = null;
            SelectionText = string.Empty;
            return;
        }

        var stock = result.Stock;
        (ChartXMin, ChartXMax) = ChartSeries.VoltageRange(stock);
        var p = Selected!;
        var e = ProposalBuilder.Estimate(stock, result.Anchors, p.Offsets);
        var stockPowerW = result.StockReference?.Averages.AvgPowerW ?? 0;
        SelectionText = Loc.Format("Results_Selection", Fmt.UvAsMv(e.CapVoltageUv), Fmt.KhzAsMhz(e.TopKhz), Fmt.Watts(e.PowerW),
            stockPowerW > 0 ? Loc.Format("Results_PowerVsStock", Fmt.SignedWatts(e.PowerW - stockPowerW)) : TileViewModel.Placeholder,
            Fmt.Percent(e.RelativePerformance));
        List<ChartSeries> series =
        [
            new() { Name = Loc.Get("Chart_Stock"), Color = ChartColor.Stock, Points = ChartSeries.CurvePoints(stock, null) },
        ];
        if (currentOffsets is not null && currentOffsets.OffsetsKhz.Count == stock.Points.Count)
            series.Add(new ChartSeries
            {
                Name = Loc.Get("Chart_Current"), Color = ChartColor.Current, Dashed = true, Thickness = 1.5,
                Points = ChartSeries.CurvePoints(stock, currentOffsets),
            });
        series.Add(new ChartSeries
        {
            Name = SelectedCard.Title, Color = ChartColor.Accent, Thickness = 2.5,
            Points = ChartSeries.CurvePoints(stock, p.Offsets),
        });
        series.Add(new ChartSeries
        {
            Name = Loc.Get("Chart_Measured"), Color = ChartColor.Success, ShowLine = false, MarkerSize = 7,
            Points = [.. result.Anchors.OrderBy(a => a.VoltageUv).Select(a => new ChartPoint(a.VoltageUv / 1000.0, ProposalBuilder.ProvenKhz(a) / 1000.0))],
        });
        series.Add(new ChartSeries
        {
            Name = Loc.Get("Chart_Cap"), Color = ChartColor.Caution, ShowLine = false, MarkerSize = 12,
            Points = [new ChartPoint(Peak(stock, p.Offsets).VoltageUv / 1000.0, Peak(stock, p.Offsets).Khz / 1000.0)],
        });
        Series = series;
    }

    /// <summary>Reads the offsets on the GPU for the chart's "current" line.</summary>
    public async Task RefreshCurrentAsync()
    {
        var gpu = session.Gpu;
        if (gpu is null) return;
        try
        {
            currentOffsets = await Task.Run(gpu.ReadOffsets);
        }
        catch (GpuControlException)
        {
            currentOffsets = null;
        }

        if (result is not null) Reload();
        else BuildChart();
    }

    private void RefreshState()
    {
        NotElevated = session.Gpu is { CanWrite: false };
        ApplyCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ExportJsonCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    private bool CanWriteGpu() => SelectedCard is not null && !session.IsBusy && session.Gpu is { CanWrite: true };

    private bool CanRestore() => HasResult && !session.IsBusy && session.Gpu is { CanWrite: true };

    private bool CanUseSelection() => SelectedCard is not null && !session.IsBusy;

    private bool CanValidate() => CanWriteGpu() && !IsRaw(SelectedCard!.Proposal);

    /// <summary>True for curves not built from a cap anchor (current GPU curve, uniform offset): shifted and cut as they are.</summary>
    private static bool IsRaw(ProfileProposal p) => p.Kind is ProposalKind.Custom or ProposalKind.Uniform;

    private bool CanExport() => SelectedCard is not null;

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        IsStatusOpen = true;
    }

    /// <summary>Writes the selected proposal's offsets to the GPU.</summary>
    [RelayCommand(CanExecute = nameof(CanWriteGpu))]
    private async Task ApplyAsync()
    {
        var card = SelectedCard!;
        await WriteOffsetsAsync(Selected!.Offsets, Loc.Format("Results_AppliedMessage", card.Title));
    }

    /// <summary>Writes the offsets that were active before the optimization started.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync() =>
        await WriteOffsetsAsync(result!.OriginalOffsets, Loc.Get("Results_RestoredMessage"));

    private async Task WriteOffsetsAsync(CurveOffsets offsets, string successMessage)
    {
        var gpu = session.Gpu!;
        try
        {
            await session.RunExclusiveAsync(_ => Task.Run(() =>
            {
                gpu.ApplyOffsets(offsets);
                return true;
            }));
            ShowStatus(InfoBarSeverity.Success, Loc.Get("Results_DoneTitle"), successMessage);
        }
        catch (GpuControlException ex)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Results_ApplyFailedTitle"), ex.Message);
        }

        session.NotifyOffsetsChanged();
    }

    /// <summary>Validates the selected proposal under variable load; may raise its safety margin.</summary>
    [RelayCommand(CanExecute = nameof(CanValidate))]
    private async Task ValidateAsync()
    {
        var card = SelectedCard!;
        var current = result!;
        IsStatusOpen = false;
        IsValidating = true;
        OperationText = Loc.Get("Phase_Calibrating");
        var progress = new ValidationProgress(this);
        try
        {
            var validated = await session.RunExclusiveAsync(ct => Task.Run(() =>
                // shortcut: validation still runs the stress test in the app process; move it to the worker if it causes UI hangs.
                session.GetOptimizer().ValidateAsync(current, card.Proposal.CapAnchor, card.Proposal.Kind, progress, ct, Selected!.ExtraMarginMhz),
                CancellationToken.None));
            var updated = current with
            {
                Proposals = [.. current.Proposals.Select(p => card.Group.Contains(p.Kind) ? validated with { Kind = p.Kind } : p)],
            };
            session.SetResult(updated);
            ShowStatus(
                validated.Validated ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Loc.Get(validated.Validated ? "Results_ValidationPassedTitle" : "Results_ValidationFailedTitle"),
                Loc.Format(validated.Validated ? "Results_ValidationPassed" : "Results_ValidationFailed", validated.ExtraMarginMhz));
        }
        catch (OperationCanceledException)
        {
            ShowStatus(InfoBarSeverity.Informational, Loc.Get("Results_ValidationCancelledTitle"), Loc.Get("Results_ValidationCancelled"));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Results_ValidationErrorTitle"), ex.Message);
        }
        finally
        {
            IsValidating = false;
            session.NotifyOffsetsChanged();
        }
    }

    /// <summary>Cancels a running validation; the previous offsets are restored by the optimizer.</summary>
    [RelayCommand]
    private void CancelValidation()
    {
        session.CancelOperation();
        OperationText = Loc.Get("Phase_Cancelling");
    }

    /// <summary>Writes the selected proposal into a profile slot of the Afterburner profile file.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task SaveAsync()
    {
        var gpu = session.Gpu!;
        var store = session.ProfileStore;
        var proposal = Selected!;
        var stock = result!.Stock;

        var path = await Task.Run(() => store.FindProfileFile(gpu.Identity));
        AfterburnerProfile? profile = null;
        if (path is not null)
        {
            try
            {
                profile = await Task.Run(() => store.Read(path));
            }
            catch (IOException)
            {
            }
        }

        var dialog = new SaveProfileDialog(path, profile);
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary || path is null) return;

        var slot = dialog.SelectedSlot;
        try
        {
            var backup = await Task.Run(() =>
            {
                if (AfterburnerProcess.IsRunning()) throw new InvalidOperationException(Loc.Get("SaveProfile_RunningError"));
                var current = store.Read(path);
                var existing = current.GetCurve("Startup") ?? current.GetCurve(AfterburnerProfile.SlotSection(slot));
                var length = Math.Max(existing?.LengthBytes ?? AfterburnerVfCurve.DefaultLengthBytes, 12 + 12 * stock.Points.Count);
                var curve = AfterburnerVfCurve.Create(stock, proposal.Offsets, length);
                return store.WriteProfile(path, slot, curve, proposal.Offsets.OffsetsKhz[proposal.CapAnchor.PointIndex]);
            });
            ShowStatus(InfoBarSeverity.Success, Loc.Get("Results_SavedTitle"), Loc.Format("Results_SavedMessage", slot, backup));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Results_SaveFailedTitle"), ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportJsonAsync() => ExportAsync("json", "JSON");

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportCsvAsync() => ExportAsync("csv", "CSV");

    private async Task ExportAsync(string extension, string description)
    {
        var card = SelectedCard!;
        var stock = result!.Stock;
        var content = extension == "json"
            ? CurveExport.ToJson(stock, Selected!.Offsets)
            : CurveExport.ToCsv(stock, Selected!.Offsets);
        var name = $"rtx-curve-{card.Proposal.Kind.ToString().ToLowerInvariant()}.{extension}";

        string? path;
        try
        {
            path = await FilePickers.PickSaveAsync(name, "." + extension, description);
            if (path is null) return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"File picker unavailable: {ex.Message}");
            path = Path.Combine(FilePickers.FallbackDirectory, name);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
            ShowStatus(InfoBarSeverity.Success, Loc.Get("Results_ExportedTitle"), Loc.Format("Results_ExportedMessage", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Results_ExportFailedTitle"), ex.Message);
        }
    }

    /// <summary>Progress sink of a validation: shows phase changes and the elapsed test time.</summary>
    private sealed class ValidationProgress(ResultsViewModel vm) : IProgress<OptimizerEvent>
    {
        private readonly Stopwatch throttle = Stopwatch.StartNew();
        private TimeSpan duration = TimeSpan.Zero;

        public void Report(OptimizerEvent value)
        {
            switch (value)
            {
                case PhaseChanged p:
                    vm.session.Dispatcher.TryEnqueue(() => vm.OperationText = Loc.Get("Phase_" + p.Phase));
                    break;
                case TestStarted t:
                    duration = t.Duration;
                    break;
                case StressProgressed s when throttle.ElapsedMilliseconds >= 250 && duration > TimeSpan.Zero:
                    throttle.Restart();
                    var text = Loc.Format("Results_ValidatingProgress", Fmt.Duration(s.Progress.Elapsed), Fmt.Duration(duration));
                    vm.session.Dispatcher.TryEnqueue(() => vm.OperationText = text);
                    break;
            }
        }
    }
}
