using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RtxAutoCurveOptimizer.App.Services;
using RtxAutoCurveOptimizer.App.ViewModels;

namespace RtxAutoCurveOptimizer.App.Views;

/// <summary>Optimize page; keeps the event log scrolled to the newest line.</summary>
public sealed partial class OptimizePage : Page
{
    public OptimizePage()
    {
        InitializeComponent();
        ViewModel.Log.CollectionChanged += OnLogChanged;
    }

    public OptimizeViewModel ViewModel { get; } = AppViewModels.Current.Optimize;

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            LogScroller.UpdateLayout();
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null, true);
        });
    }

    private void ViewResults_Click(object sender, RoutedEventArgs e) => AppNavigation.Go("results");
}
