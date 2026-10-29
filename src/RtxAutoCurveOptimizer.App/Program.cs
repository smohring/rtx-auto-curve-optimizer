using RtxAutoCurveOptimizer.App.Services;
using RtxAutoCurveOptimizer.Optimization;

namespace RtxAutoCurveOptimizer.App;

/// <summary>
/// Entry point. <c>--watchdog</c> and <c>--worker</c> run without WinUI, so a driver reset that takes down the
/// UI's rendering cannot take them down too; everything else starts the app.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppDataFolder.MigrateLegacy();
        var mode = args.FirstOrDefault()?.ToLowerInvariant();
        if (mode == "--watchdog")
        {
            WatchdogHost.RunAsync().GetAwaiter().GetResult();
            return 0;
        }

        if (mode == "--worker" && args.Length > 1) return WorkerHost.Run(args[1]);

        // Same as the generated WinUI Main.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(queue));
            _ = new App();
        });
        return 0;
    }
}
