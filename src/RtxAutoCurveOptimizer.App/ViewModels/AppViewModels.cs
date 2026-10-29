using RtxAutoCurveOptimizer.App.Services;

namespace RtxAutoCurveOptimizer.App.ViewModels;

/// <summary>The view models of all pages; they live as long as the process so page state survives navigation.</summary>
public sealed class AppViewModels
{
    private AppViewModels(GpuSession session)
    {
        Overview = new OverviewViewModel(session);
        Optimize = new OptimizeViewModel(session);
        Results = new ResultsViewModel(session);
    }

    public static AppViewModels Current { get; private set; } = null!;

    public OverviewViewModel Overview { get; }

    public OptimizeViewModel Optimize { get; }

    public ResultsViewModel Results { get; }

    /// <summary>Creates the view models once the session exists.</summary>
    public static void Create(GpuSession session) => Current = new AppViewModels(session);
}
