using System.Text.Json;

namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>
/// Liveness signal of a running optimization: written by the app every second, read by the watchdog.
/// LastProgress moves only when the optimizer reports progress, so a process stuck in a driver call goes stale.
/// </summary>
public sealed record Heartbeat(
    int ProcessId, DateTimeOffset LastProgress, bool Finished, int Restarts, IReadOnlyList<string> RestartArguments);

public enum WatchdogAction
{
    /// <summary>The app is alive and making progress.</summary>
    Wait,
    /// <summary>No run is active any more; the watchdog ends.</summary>
    Exit,
    /// <summary>The app died; start it again with --resume.</summary>
    Restart,
    /// <summary>The app hangs; kill it, then start it again with --resume.</summary>
    KillAndRestart,
    /// <summary>The app hangs and the restart budget is used up; kill it and end.</summary>
    Kill,
    /// <summary>The app died and the restart budget is used up; end.</summary>
    GiveUp,
}

/// <summary>Decides what the watchdog does with the current heartbeat.</summary>
public static class WatchdogPolicy
{
    /// <summary>No progress for this long means the app hangs (longest silent step: driver recovery plus recalibration).</summary>
    public static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(90);

    public const int MaxRestarts = 3;

    /// <summary>Wait for the driver before restart number <paramref name="restarts"/> + 1: 15 s, 30 s, 45 s.</summary>
    public static TimeSpan RecoveryWait(int restarts) => TimeSpan.FromSeconds(15 * (restarts + 1));

    /// <summary>The heartbeat after a restart that never came up: the attempt counts against the budget.</summary>
    public static Heartbeat AfterFailedStart(Heartbeat beat) => beat with { Restarts = beat.Restarts + 1 };

    public static WatchdogAction Decide(Heartbeat? beat, bool processAlive, DateTimeOffset now)
    {
        if (beat is null || beat.Finished) return WatchdogAction.Exit;
        var budgetLeft = beat.Restarts < MaxRestarts;
        if (!processAlive) return budgetLeft ? WatchdogAction.Restart : WatchdogAction.GiveUp;
        if (now - beat.LastProgress <= HangTimeout) return WatchdogAction.Wait;
        return budgetLeft ? WatchdogAction.KillAndRestart : WatchdogAction.Kill;
    }
}

/// <summary>Reads and writes the heartbeat file atomically; unreadable content counts as no heartbeat.</summary>
public sealed class HeartbeatFile(string path)
{
    public static string DefaultPath { get; } = Path.Combine(AppDataFolder.Path, "heartbeat.json");

    public string FilePath { get; } = path;

    public void Write(Heartbeat beat)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(beat));
        File.Move(temp, FilePath, overwrite: true);
    }

    public Heartbeat? Read()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Heartbeat>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
