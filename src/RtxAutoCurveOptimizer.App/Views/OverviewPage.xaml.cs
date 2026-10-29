using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RtxAutoCurveOptimizer.App.ViewModels;

namespace RtxAutoCurveOptimizer.App.Views;

/// <summary>Overview page; samples telemetry only while it is the visible page.</summary>
public sealed partial class OverviewPage : Page
{
    public OverviewPage()
    {
        InitializeComponent();
        Scroller.SizeChanged += (_, _) => Root.MinHeight = Scroller.ActualHeight;
    }

    public OverviewViewModel ViewModel { get; } = AppViewModels.Current.Overview;

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.StartSampling();

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.StopSampling();
}
