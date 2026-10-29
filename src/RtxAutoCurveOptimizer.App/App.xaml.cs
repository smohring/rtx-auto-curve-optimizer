using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Globalization;
using RtxAutoCurveOptimizer.App.Services;
using RtxAutoCurveOptimizer.Optimization;

namespace RtxAutoCurveOptimizer.App;

/// <summary>Application entry point: applies command-line options, opens the GPU session and shows the main window.</summary>
public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        Options = AppOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToList());
        ApplyLanguage(Options.Language);
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    public AppOptions Options { get; }

    /// <summary>Writes an unhandled exception to %LOCALAPPDATA%\RtxAutoCurveOptimizer\crash.log.</summary>
    private static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = AppDataFolder.Path;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:s} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Sets the UI and number-format culture when a language override is given.</summary>
    private static void ApplyLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return;
        ApplicationLanguages.PrimaryLanguageOverride = language;
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        GpuSession.Create(Options, DispatcherQueue.GetForCurrentThread());
        window = new MainWindow();
        window.Activate();
    }
}
