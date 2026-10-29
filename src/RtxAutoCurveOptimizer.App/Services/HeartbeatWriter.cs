using System.Diagnostics;
using RtxAutoCurveOptimizer.Optimization;

namespace RtxAutoCurveOptimizer.App.Services;

/// <summary>
/// Writes the heartbeat every second while a run is active and starts the watchdog process. Progress is marked
/// by a UI-thread timer: the heartbeat says the app is alive; the app itself supervises the worker process.
/// </summary>
public sealed class HeartbeatWriter : IDisposable
{
    private readonly HeartbeatFile file = new(HeartbeatFile.DefaultPath);
    private readonly AppOptions options;
    private readonly Lock gate = new();
    private Timer? timer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? uiTimer;
    private long lastProgressTicks;
    private volatile bool done;

    public HeartbeatWriter(AppOptions options) => this.options = options;

    /// <summary>Starts writing and launches the watchdog unless one is already watching.</summary>
    public void Start()
    {
        done = false;
        Touch();
        Write(finished: false);
        lock (gate) timer ??= new Timer(_ => Write(finished: false), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        if (uiTimer is null && Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            uiTimer = queue.CreateTimer();
            uiTimer.Interval = TimeSpan.FromSeconds(1);
            uiTimer.Tick += (_, _) => Touch();
            uiTimer.Start();
        }
        StartWatchdog();
    }

    /// <summary>Marks progress; called for every optimizer event, from any thread.</summary>
    public void Touch() => Interlocked.Exchange(ref lastProgressTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>Stops writing and marks the run as finished so the watchdog ends.</summary>
    public void Finish()
    {
        done = true;
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
        }
        uiTimer?.Stop();
        uiTimer = null;
        Write(finished: true);
    }

    public void Dispose() => Finish();

    private void Write(bool finished)
    {
        // A timer tick that is still running when Finish disposes the timer must not undo "finished".
        if (done && !finished) return;
        try
        {
            var last = new DateTimeOffset(Interlocked.Read(ref lastProgressTicks), TimeSpan.Zero);
            file.Write(new Heartbeat(Environment.ProcessId, last, finished, options.Restarts, options.PassThrough));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void StartWatchdog()
    {
        if (WatchdogHost.IsRunning() || Environment.ProcessPath is not { } exe) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, ["--watchdog"]) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Without a watchdog the run still works; a crash then needs a manual restart.
        }
    }
}
