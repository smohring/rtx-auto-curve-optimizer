using System.Diagnostics;
using RtxAutoCurveOptimizer.Optimization;

namespace RtxAutoCurveOptimizer.App.Services;

/// <summary>
/// The windowless watchdog process (<c>--watchdog</c>): restarts a crashed app with <c>--resume</c>, kills and
/// restarts an app whose run makes no progress, and ends when the run finishes or the restart budget is used up
/// (a restart that never comes up counts against the budget).
/// </summary>
public static class WatchdogHost
{
    private const string MutexName = @"Local\RtxAutoCurveOptimizer.Watchdog";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>True if a watchdog process already holds the mutex.</summary>
    public static bool IsRunning()
    {
        if (Mutex.TryOpenExisting(MutexName, out var existing))
        {
            existing.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>Watches the heartbeat until the run ends; returns immediately if another watchdog is active.</summary>
    public static async Task RunAsync()
    {
        using var mutex = new Mutex(true, MutexName, out var owned);
        if (!owned) return;

        var file = new HeartbeatFile(HeartbeatFile.DefaultPath);
        Log("watchdog started");
        while (true)
        {
            await Task.Delay(PollInterval);
            var beat = file.Read();
            var alive = beat is not null && IsAlive(beat.ProcessId);
            var action = WatchdogPolicy.Decide(beat, alive, DateTimeOffset.UtcNow);
            if (action == WatchdogAction.Wait) continue;
            if (action == WatchdogAction.Exit)
            {
                Log("run finished; watchdog ends");
                return;
            }

            Log($"app {beat!.ProcessId}: {action} (restarts so far {beat.Restarts}, last progress {beat.LastProgress:O})");
            if (action is WatchdogAction.Kill or WatchdogAction.KillAndRestart && !Kill(beat.ProcessId))
            {
                Log("the app could not be terminated; the system needs a restart");
                return;
            }

            if (action is WatchdogAction.Kill or WatchdogAction.GiveUp)
            {
                Log("restart budget used up; start the app manually to recover the run");
                return;
            }

            await Task.Delay(WatchdogPolicy.RecoveryWait(beat.Restarts));
            if (!await RestartAsync(file, beat)) file.Write(WatchdogPolicy.AfterFailedStart(beat));
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool Kill(int pid)
    {
        try
        {
            // Not the whole tree: the app started this watchdog, so it is part of that tree. A process stuck in the
            // driver only exits when the driver lets go, hence the long wait.
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: false);
            return process.WaitForExit(KillTimeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Starts the app with --resume and waits until it writes a heartbeat of its own; false if it never does.</summary>
    private static async Task<bool> RestartAsync(HeartbeatFile file, Heartbeat beat)
    {
        if (Environment.ProcessPath is not { } exe) return false;
        string[] args = ["--resume", "--restarts", (beat.Restarts + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), .. beat.RestartArguments];
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log($"restart failed: {ex.Message}");
            return false;
        }

        Log($"restarted the app ({beat.Restarts + 1} of {WatchdogPolicy.MaxRestarts})");
        var deadline = DateTimeOffset.UtcNow + StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
            if (file.Read() is { } next && next.ProcessId != beat.ProcessId) return true;
        }

        Log("the restarted app did not resume the run in time");
        return false;
    }

    /// <summary>Appends to %LOCALAPPDATA%\RtxAutoCurveOptimizer\watchdog.log.</summary>
    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(AppDataFolder.Path, "watchdog.log");
            File.AppendAllText(path, $"{DateTime.Now:s} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
