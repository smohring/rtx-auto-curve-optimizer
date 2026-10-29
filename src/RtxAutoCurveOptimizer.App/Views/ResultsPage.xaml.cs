using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RtxAutoCurveOptimizer.App.Services;
using RtxAutoCurveOptimizer.App.ViewModels;

namespace RtxAutoCurveOptimizer.App.Views;

/// <summary>Results page; re-reads the GPU's current offsets whenever it is shown.</summary>
public sealed partial class ResultsPage : Page
{
    public ResultsPage()
    {
        InitializeComponent();
    }

    public ResultsViewModel ViewModel { get; } = AppViewModels.Current.Results;

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.RefreshCurrentAsync();

    private void Chart_PlotClicked(double voltageMv, double clockMhz) => ViewModel.ClockLimitMhz = clockMhz;

    private void GoOptimize_Click(object sender, RoutedEventArgs e) => AppNavigation.Go("optimize");
}
