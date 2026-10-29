using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RtxAutoCurveOptimizer.App.Services;
using RtxAutoCurveOptimizer.App.ViewModels;
using RtxAutoCurveOptimizer.App.Views;
using RtxAutoCurveOptimizer.Optimization;
using Windows.Graphics;

namespace RtxAutoCurveOptimizer.App;

/// <summary>Main window: Mica backdrop, custom title bar, navigation shell, crash recovery and close protection.</summary>
public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["overview"] = typeof(OverviewPage),
        ["optimize"] = typeof(OptimizePage),
        ["results"] = typeof(ResultsPage),
        ["settings"] = typeof(SettingsPage),
    };

    private readonly GpuSession session = GpuSession.Current;
    private bool closeConfirmed;
    private bool closing;

    public MainWindow()
    {
        AppViewModels.Create(session);
        InitializeComponent();
        Title = Loc.Get("AppTitle");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        Dialogs.WindowId = AppWindow.Id;
        ApplyInitialSize();

        AppWindow.Closing += OnClosing;
        Closed += (_, _) => session.Dispose();
        AppNavigation.Requested += Navigate;
        ((FrameworkElement)Content).Loaded += OnContentLoaded;
        foreach (var item in Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>())
            item.SizeChanged += (s, _) => FitSelectionIndicator((NavigationViewItem)s);
    }

    /// <summary>Stretches a top tab's selection bar (16 px in the WinUI template) to the width of its label.</summary>
    private static void FitSelectionIndicator(NavigationViewItem item)
    {
        var label = Descendants(item).OfType<ContentPresenter>().FirstOrDefault(c => c.Name == "ContentPresenter");
        var bar = Descendants(item).OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().FirstOrDefault(r => r.Name == "SelectionIndicator");
        if (label is null || bar is null || bar.Parent is not Grid { Parent: UIElement root } grid) return;
        grid.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Margin = new Thickness(label.TransformToVisual(root).TransformPoint(default).X, 0, 0, grid.Margin.Bottom);
        bar.Width = label.ActualWidth;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private void ApplyInitialSize()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = (int)Math.Min(1360 * scale, area.Width - 40);
        var height = (int)Math.Min(900 * scale, area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(1100 * scale);
            presenter.PreferredMinimumHeight = (int)(700 * scale);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private async void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)Content).Loaded -= OnContentLoaded;
        Dialogs.Root = Content.XamlRoot;
        ((FrameworkElement)Content).RequestedTheme = session.Options.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        Navigate(session.Options.StartPage ?? "overview");
        try
        {
            await CheckForUnfinishedSessionAsync();
            if (session.Options.AutoStart && AppViewModels.Current.Optimize.StartCommand.CanExecute(null))
            {
                Navigate("optimize");
                AppViewModels.Current.Optimize.StartCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            ShowShell(InfoBarSeverity.Error, Loc.Get("Recovery_ErrorTitle"), ex.Message);
        }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag && Pages.TryGetValue(tag, out var page) && ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    /// <summary>Selects the navigation item with the given tag.</summary>
    private void Navigate(string tag)
    {
        var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems)
            .OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
        if (item is not null) Navigation.SelectedItem = item;
    }

    private void ShowShell(InfoBarSeverity severity, string title, string message)
    {
        ShellBar.Severity = severity;
        ShellBar.Title = title;
        ShellBar.Message = message;
        ShellBar.IsOpen = true;
    }

    /// <summary>Offers to restore the original offsets and resume if a previous run did not finish.</summary>
    private async Task CheckForUnfinishedSessionAsync()
    {
        if (session.Gpu is not { } gpu) return;
        var recovery = new SessionRecovery(session.Store);
        var unfinished = await Task.Run(recovery.FindUnfinished);
        if (unfinished is null) return;

        var started = unfinished.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (!gpu.CanWrite)
        {
            ShowShell(InfoBarSeverity.Warning, Loc.Get("Recovery_NeedsAdminTitle"), Loc.Format("Recovery_NeedsAdmin", started));
            return;
        }

        var answer = session.Options.AutoResume
            ? ContentDialogResult.Primary
            : await Dialogs.ShowAsync(
                Loc.Get("Recovery_Title"),
                Loc.Format("Recovery_Message", started, unfinished.Tests.Count, unfinished.Anchors.Count),
                Loc.Get("Recovery_Resume"), Loc.Get("Recovery_RestoreOnly"), Loc.Get("Recovery_Ignore"));
        if (answer == ContentDialogResult.None) return;

        var outcome = await Task.Run(() => recovery.Recover(unfinished, gpu));
        session.NotifyOffsetsChanged();
        switch (outcome.Restore)
        {
            case RecoveryRestore.Restored:
                ShowShell(InfoBarSeverity.Success, Loc.Get("Recovery_RestoredTitle"),
                    Loc.Get(outcome.CrashedTestRecorded ? "Recovery_RestoredCrash" : "Recovery_Restored"));
                break;
            case RecoveryRestore.GpuMismatch:
                ShowShell(InfoBarSeverity.Warning, Loc.Get("Recovery_MismatchTitle"), Loc.Get("Recovery_Mismatch"));
                return;
            default:
                ShowShell(InfoBarSeverity.Error, Loc.Get("Recovery_FailedTitle"), outcome.Detail ?? string.Empty);
                return;
        }

        if (outcome.Session.Anchors.Count > 0) session.SetResult(outcome.Session.ToResult());
        if (answer == ContentDialogResult.Primary)
        {
            Navigate("optimize");
            await AppViewModels.Current.Optimize.ResumeAsync(outcome.Session);
        }
    }

    /// <summary>While an operation runs the window closes only after the user confirms and the original offsets are restored.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeConfirmed || !session.IsBusy) return;
        args.Cancel = true;
        if (closing) return;
        closing = true;
        try
        {
            var answer = await Dialogs.ShowAsync(
                Loc.Get("Dialog_CloseTitle"), Loc.Get("Dialog_CloseMessage"),
                Loc.Get("Dialog_CloseConfirm"), null, Loc.Get("Dialog_Cancel"), ContentDialogButton.Close);
            if (answer != ContentDialogResult.Primary) return;

            Navigation.IsEnabled = false;
            ShowShell(InfoBarSeverity.Informational, Loc.Get("Dialog_ClosingTitle"), Loc.Get("Dialog_ClosingMessage"));
            session.CancelOperation();
            await session.WaitIdleAsync();
            closeConfirmed = true;
            Close();
        }
        finally
        {
            closing = false;
            Navigation.IsEnabled = true;
        }
    }
}
