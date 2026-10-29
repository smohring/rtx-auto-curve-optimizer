using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Cli;

/// <summary>One command: its option names, argument counts, help text and handler.</summary>
internal sealed record CommandSpec(
    string Name, string Usage, string Summary,
    string[] ValueOptions, string[] FlagOptions, int MinPositionals, int MaxPositionals,
    Func<CommandArgs, CancellationToken, Task<int>> Run);

/// <summary>Command dispatch, Ctrl+C handling and the one-line error output.</summary>
internal static class CommandLine
{
    private static readonly CommandSpec[] Commands =
    [
        new("info", "info [--json]", "identity, curve, telemetry, Afterburner state (read-only)",
            [], ["json"], 0, 0, InfoCommand.RunAsync),
        new("stress", "stress --seconds N [--pattern steady|pulsed|variable] [--intensity 0.1-1.0]",
            "stress test without touching the curve (loads the GPU)",
            ["seconds", "pattern", "intensity"], [], 0, 0, StressCommand.RunAsync),
        new("simulate", "simulate [--preset quick|standard|thorough] [--seed n] [--power-cap W] [--tests]",
            "full optimizer run on the simulated GPU (virtual clock)",
            ["preset", "seed", "power-cap"], ["tests"], 0, 0, SimulateCommand.RunAsync),
        new("run", "run [--preset quick|standard|thorough] [--margin MHz] [--force]",
            "real optimization (administrator; loads the GPU for minutes to hours; refuses while Afterburner runs unless --force)",
            ["preset", "margin"], ["force"], 0, 0, RunCommand.RunAsync),
        new("apply", "apply <session.json> [--proposal efficient|maxefficiency|performance]",
            "apply a proposal of a session to the GPU (administrator)",
            ["proposal"], [], 1, 1, CurveCommands.ApplyAsync),
        new("reset", "reset", "set all offsets to zero (administrator)",
            [], [], 0, 0, CurveCommands.ResetAsync),
        new("restore", "restore <session.json>", "restore the original offsets of a session (administrator)",
            [], [], 1, 1, CurveCommands.RestoreAsync),
        new("write-test", "write-test",
            "prove the curve write path: original offsets, original - 15 MHz, original again (administrator; no GPU load)",
            [], [], 0, 0, WriteTestCommand.RunAsync),
        new("export", "export <session.json> --proposal ... [--format csv|json] [--out path]",
            "write a proposal's curve as CSV or JSON (stdout without --out)",
            ["proposal", "format", "out"], [], 1, 1, FileCommands.ExportAsync),
        new("recompute", "recompute <session.json> [--margin MHz]",
            "rebuild a session's recommendations and proposals with the current rules (optionally another safety margin); saved as <id>-recomputed",
            ["margin"], [], 1, 1, FileCommands.RecomputeAsync),
        new("afterburner", "afterburner <session.json> --proposal ... --slot 1..5 [--force] [--profiles dir]",
            "write a proposal into an Afterburner profile slot (refuses while Afterburner runs unless --force)",
            ["proposal", "slot", "profiles"], ["force"], 1, 1, FileCommands.AfterburnerAsync),
    ];

    /// <summary>Runs the command named by the first argument and returns the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        var tokens = args.Where(a => !a.Equals("--verbose", StringComparison.OrdinalIgnoreCase)).ToList();

        if (tokens.Count == 0 || tokens[0] is "-h" or "--help" or "help" or "/?")
        {
            Console.WriteLine(HelpText());
            return tokens.Count == 0 ? ExitCodes.Usage : ExitCodes.Success;
        }

        var spec = Commands.FirstOrDefault(c => c.Name.Equals(tokens[0], StringComparison.OrdinalIgnoreCase));
        if (spec is null)
        {
            Console.Error.WriteLine($"rtxaco: unknown command '{tokens[0]}'. Run 'rtxaco --help'.");
            return ExitCodes.Usage;
        }

        if (tokens.Skip(1).Any(t => t is "-h" or "--help"))
        {
            Console.WriteLine($"rtxaco {spec.Usage}\n  {spec.Summary}");
            return ExitCodes.Success;
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var parsed = CommandArgs.Parse(tokens.Skip(1).ToList(), spec.ValueOptions, spec.FlagOptions, spec.MinPositionals, spec.MaxPositionals);
            return await spec.Run(parsed, cts.Token);
        }
        catch (CliException ex)
        {
            return Fail(ex.Message, ex.ExitCode, ex, verbose);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("rtxaco: cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (GpuAccessDeniedException ex)
        {
            return Fail($"access denied: {ex.Message}", ExitCodes.NotElevated, ex, verbose);
        }
        catch (GpuControlException ex)
        {
            var status = ex.NvapiStatus is { } s ? $" (NVAPI status {s})" : "";
            return Fail($"GPU error: {ex.Message}{status}", ExitCodes.Failure, ex, verbose);
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}", ExitCodes.Failure, ex, verbose);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static int Fail(string message, int exitCode, Exception ex, bool verbose)
    {
        Console.Error.WriteLine($"rtxaco: {message}");
        if (verbose) Console.Error.WriteLine(ex);
        return exitCode;
    }

    private static string HelpText()
    {
        var lines = new List<string>
        {
            "rtxaco - RTX Auto Curve Optimizer command line",
            "",
            "Usage: rtxaco <command> [options]   (add --verbose for stack traces)",
            "",
        };
        lines.AddRange(Commands.Select(c => $"  {c.Usage}\n      {c.Summary}"));
        lines.Add("");
        lines.Add("Exit codes: 0 ok, 1 failure, 2 administrator rights needed, 3 usage error, 130 cancelled.");
        lines.Add("Only stress and run put the GPU under load; info, simulate and export are read-only.");
        lines.Add("write-test changes the curve briefly (never to a less stable state) and always restores the original offsets.");
        return string.Join('\n', lines);
    }
}
