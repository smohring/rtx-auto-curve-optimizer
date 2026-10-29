using System.Globalization;

namespace RtxAutoCurveOptimizer.App.Services;

/// <summary>
/// Command-line options: <c>--simulate</c>, <c>--lang xx-XX</c>, <c>--theme light|dark</c>, <c>--speed N</c>, <c>--page name</c>,
/// <c>--autostart</c>, and from the watchdog <c>--resume</c>, <c>--restarts N</c> (<c>--watchdog</c>/<c>--worker</c> are handled in Program).
/// </summary>
public sealed class AppOptions
{
    /// <summary>Starts with the simulated GPU instead of the real one.</summary>
    public bool Simulate { get; private init; }

    /// <summary>UI language override (BCP-47 tag), or null for the system language.</summary>
    public string? Language { get; private init; }

    /// <summary>Real-time speed-up of simulated stress tests (virtual seconds per real second).</summary>
    public double SimulationSpeed { get; private init; } = 10;

    /// <summary>Navigation tag of the page to open at startup (overview, optimize, results, settings).</summary>
    public string? StartPage { get; private init; }

    /// <summary>Forces the light or dark theme (<c>--theme light|dark</c>); null follows the system.</summary>
    public string? Theme { get; private init; }

    /// <summary>Starts a Quick optimization right after startup (demo and screenshot runs).</summary>
    public bool AutoStart { get; private init; }

    /// <summary>Recovers and resumes an unfinished run without asking (set by the watchdog on restart).</summary>
    public bool AutoResume { get; private init; }

    /// <summary>How often the watchdog has restarted the app for the current run.</summary>
    public int Restarts { get; private init; }

    /// <summary>Arguments to hand to a restarted app (simulation, language, theme, speed).</summary>
    public IReadOnlyList<string> PassThrough { get; private init; } = [];

    /// <summary>Parses the process command line; unknown arguments are ignored.</summary>
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var simulate = false;
        string? language = null, page = null, theme = null;
        var speed = 10.0;
        var autoStart = false;
        var autoResume = false;
        var restarts = 0;
        var passThrough = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--simulate": simulate = true; passThrough.Add(args[i]); break;
                case "--autostart": autoStart = true; break;
                case "--resume": autoResume = true; break;
                case "--restarts" when i + 1 < args.Count && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var r):
                    restarts = r;
                    i++;
                    break;
                case "--lang" when i + 1 < args.Count: language = args[++i]; passThrough.AddRange(["--lang", language]); break;
                case "--theme" when i + 1 < args.Count: theme = args[++i].ToLowerInvariant(); passThrough.AddRange(["--theme", theme]); break;
                case "--page" when i + 1 < args.Count: page = args[++i].ToLowerInvariant(); break;
                case "--speed" when i + 1 < args.Count
                    && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0:
                    speed = s;
                    passThrough.AddRange(["--speed", args[i + 1]]);
                    i++;
                    break;
            }
        }

        return new AppOptions
        {
            Simulate = simulate, Language = language, SimulationSpeed = speed, StartPage = page, AutoStart = autoStart, Theme = theme,
            AutoResume = autoResume, Restarts = restarts, PassThrough = passThrough,
        };
    }
}
