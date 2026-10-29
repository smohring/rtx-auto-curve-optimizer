using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Hardware;
using RtxAutoCurveOptimizer.Optimization;
using RtxAutoCurveOptimizer.Stress;

namespace RtxAutoCurveOptimizer.App.Services;

/// <summary>What a worker process runs: a new optimization with these settings, or the resume of a recovered session.</summary>
public sealed record WorkerRequest(OptimizerSettings? Settings, string? ResumeSessionId);

/// <summary>
/// The worker process (<c>--worker request.json</c>): runs the optimization on the real GPU and writes every event as
/// one JSON line to stdout. "cancel" on stdin cancels; a closed stdin means the app is gone and the worker exits at
/// once, leaving the session resumable.
/// </summary>
public static class WorkerHost
{
    public static int Run(string requestPath)
    {
        var request = JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllText(requestPath))!;
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new Lock();
        void Send(OptimizerEvent e)
        {
            lock (gate) output.WriteLine(JsonSerializer.Serialize(e));
        }

        using var cts = new CancellationTokenSource();
        var input = new Thread(() =>
        {
            while (Console.In.ReadLine() is { } line)
                if (line == "cancel") cts.Cancel();
            Environment.Exit(3);
        }) { IsBackground = true };
        input.Start();

        using var gpu = NvidiaGpu.OpenFirst();
        using var tester = new ComputeStressTester();
        var store = new JsonSessionStore();
        var optimizer = new CurveOptimizer(gpu, tester, store);
        var progress = new Progress(Send);
        var result = request.ResumeSessionId is { } id
            ? optimizer.ResumeAsync(store.Load(id) ?? throw new InvalidOperationException($"Session {id} not found."), progress, cts.Token).GetAwaiter().GetResult()
            : optimizer.RunAsync(request.Settings!, progress, cts.Token).GetAwaiter().GetResult();
        Send(new RunFinished(result.SessionId));
        return 0;
    }

    private sealed class Progress(Action<OptimizerEvent> send) : IProgress<OptimizerEvent>
    {
        public void Report(OptimizerEvent value) => send(value);
    }
}

/// <summary>
/// App side of the worker: starts it, forwards its events, and supervises it. A worker that dies or sends nothing
/// for <see cref="WatchdogPolicy.HangTimeout"/> is killed, the session recovered, and a new worker resumes it.
/// </summary>
public static class WorkerClient
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromMinutes(10);

    public static async Task<OptimizationResult> RunAsync(
        WorkerRequest request, IGpuControl gpu, ISessionStore store, IProgress<OptimizerEvent> progress, CancellationToken ct)
    {
        for (var restarts = 0; ; restarts++)
        {
            var finished = await RunOnceAsync(request, progress, ct);
            if (finished is not null && store.Load(finished) is { } done) return done.ToResult();
            if (ct.IsCancellationRequested || restarts >= WatchdogPolicy.MaxRestarts)
                throw new InvalidOperationException("The test process ended without a result.");

            progress.Report(new Warning(WarningKind.WorkerRestarted, (restarts + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await Task.Delay(RestartDelay, ct);
            try
            {
                gpu.Reinitialize();
            }
            catch (GpuControlException)
            {
                // The worker reconnects on its own; recovery below then reports a failed restore.
            }
            var recovery = new SessionRecovery(store);
            var unfinished = recovery.FindUnfinished() ?? throw new InvalidOperationException("No unfinished session to resume.");
            request = new WorkerRequest(null, recovery.Recover(unfinished, gpu).Session.Id);
        }
    }

    /// <summary>Runs one worker to its end; returns the finished session id, or null if it died or hung.</summary>
    private static async Task<string?> RunOnceAsync(WorkerRequest request, IProgress<OptimizerEvent> progress, CancellationToken ct)
    {
        var requestPath = Path.Combine(AppDataFolder.Path, "worker-request.json");
        Directory.CreateDirectory(AppDataFolder.Path);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), CancellationToken.None);

        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["--worker", requestPath])
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
        })!;
        using var cancel = ct.Register(() =>
        {
            try
            {
                process.StandardInput.WriteLine("cancel");
                process.StandardInput.Flush();
            }
            catch (IOException)
            {
            }
        });

        string? finished = null;
        var lastEvent = Stopwatch.StartNew();
        var reader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                lastEvent.Restart();
                if (JsonSerializer.Deserialize<OptimizerEvent>(line) is not { } e) continue;
                if (e is RunFinished f) finished = f.SessionId;
                else progress.Report(e);
            }
        }, CancellationToken.None);

        while (await Task.WhenAny(reader, Task.Delay(2000, CancellationToken.None)) != reader)
        {
            if (lastEvent.Elapsed < WatchdogPolicy.HangTimeout) continue;
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            // A worker stuck inside the driver only dies when the driver lets go.
            await process.WaitForExitAsync(new CancellationTokenSource(KillTimeout).Token).ContinueWith(_ => { }, TaskScheduler.Default);
            // Never start a second worker next to one that may still wake up and write the GPU and the session.
            if (!process.HasExited)
                throw new InvalidOperationException("The test process is stuck in the graphics driver. Restart the PC, then start the app to resume.");
            return null;
        }

        await process.WaitForExitAsync(CancellationToken.None);
        return finished;
    }
}
