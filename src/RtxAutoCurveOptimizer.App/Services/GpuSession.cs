using Microsoft.UI.Dispatching;
using RtxAutoCurveOptimizer.Afterburner;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Hardware;
using RtxAutoCurveOptimizer.Optimization;
using RtxAutoCurveOptimizer.Optimization.Simulation;
using RtxAutoCurveOptimizer.Stress;

namespace RtxAutoCurveOptimizer.App.Services;

/// <summary>
/// Shared application state: the GPU (real or simulated), the stress tester, the optimizer, the session store,
/// the last optimization result and the one-operation-at-a-time gate. All members are used from the UI thread
/// unless stated otherwise.
/// </summary>
public sealed class GpuSession : IDisposable
{
    private readonly Lock gate = new();
    private IStressTester? tester;
    private CurveOptimizer? optimizer;
    private CancellationTokenSource? operationCts;
    private TaskCompletionSource? idle;

    private GpuSession(AppOptions options, DispatcherQueue dispatcher)
    {
        Options = options;
        Dispatcher = dispatcher;
        Store = new JsonSessionStore();
        ProfileStore = AfterburnerProfileStore.CreateDefault();
    }

    /// <summary>The single session of the process.</summary>
    public static GpuSession Current { get; private set; } = null!;

    public AppOptions Options { get; }

    public DispatcherQueue Dispatcher { get; }

    public bool DemoMode { get; private set; }

    /// <summary>The opened GPU, or null if none could be opened (see <see cref="GpuError"/>).</summary>
    public IGpuControl? Gpu { get; private set; }

    public string? GpuError { get; private set; }

    /// <summary>Stock curve read when the GPU was opened.</summary>
    public StockCurve? Stock { get; private set; }

    public ISessionStore Store { get; private set; }

    public AfterburnerProfileStore ProfileStore { get; private set; }

    public TimeProvider Time { get; private set; } = TimeProvider.System;

    /// <summary>Result of the latest optimization (or recovered session) that produced usable data.</summary>
    public OptimizationResult? LastResult { get; private set; }

    /// <summary>True while an optimization, validation or similar GPU operation runs.</summary>
    public bool IsBusy { get; private set; }

    public bool CanWrite => Gpu?.CanWrite == true;

    /// <summary>Raised after the GPU session was (re)opened, e.g. when demo mode is switched.</summary>
    public event Action? SessionChanged;

    public event Action? ResultChanged;

    public event Action? BusyChanged;

    /// <summary>Raised after the app changed the GPU's curve offsets (apply, restore).</summary>
    public event Action? OffsetsChanged;

    /// <summary>Tells listeners that the curve offsets on the GPU changed.</summary>
    public void NotifyOffsetsChanged() => OffsetsChanged?.Invoke();

    /// <summary>Creates the process-wide session and opens the GPU according to the options.</summary>
    public static GpuSession Create(AppOptions options, DispatcherQueue dispatcher)
    {
        Current = new GpuSession(options, dispatcher);
        Current.Open(options.Simulate);
        return Current;
    }

    /// <summary>Closes the current GPU and opens the real or the simulated one; refused while busy.</summary>
    public void Open(bool demo)
    {
        if (IsBusy) throw new InvalidOperationException("An operation is running.");
        CloseGpu();
        DemoMode = demo;
        LastResult = null;
        GpuError = null;
        try
        {
            if (demo) OpenSimulated();
            else OpenReal();
            Stock = Gpu!.ReadStockCurve();
        }
        catch (Exception ex)
        {
            Gpu?.Dispose();
            Gpu = null;
            Stock = null;
            GpuError = ex.Message;
        }

        // The Results page shows the newest finished run after a restart instead of nothing.
        LastResult = Store.List().FirstOrDefault(s => s.Status == SessionStatus.Completed && s.Anchors.Count > 0)?.ToResult();
        SessionChanged?.Invoke();
        ResultChanged?.Invoke();
    }

    private void OpenReal()
    {
        Store = new JsonSessionStore();
        ProfileStore = AfterburnerProfileStore.CreateDefault();
        Time = TimeProvider.System;
        Gpu = NvidiaGpu.OpenFirst();
    }

    private void OpenSimulated()
    {
        var root = Path.Combine(Path.GetTempPath(), "RtxAutoCurveOptimizerDemo");
        var clock = new SimulatedClock(DateTimeOffset.UtcNow);
        var gpu = new SimulatedGpu(clock, new SimulatedGpuOptions
        {
            InitialOffsetKhz = 100_000, ThermalDerateMhz = 110, HeatTimeConstant = TimeSpan.FromSeconds(15), HotLimitPenaltyMhz = 90,
        }, seed: 7);
        Gpu = gpu;
        Time = clock;
        tester = new PacedStressTester(new SimulatedStressTester(gpu, clock, seed: 7), Options.SimulationSpeed);
        Store = new JsonSessionStore(Path.Combine(root, "sessions"));

        var profiles = Path.Combine(root, "Profiles");
        Directory.CreateDirectory(profiles);
        ProfileStore = new AfterburnerProfileStore(profiles);
        var real = AfterburnerProfileStore.CreateDefault();
        if (real.FindProfileFile(gpu.Identity) is { } source)
            File.Copy(source, Path.Combine(profiles, Path.GetFileName(source)), overwrite: true);
    }

    private void CloseGpu()
    {
        lock (gate)
        {
            optimizer = null;
            tester?.Dispose();
            tester = null;
        }

        Gpu?.Dispose();
        Gpu = null;
    }

    /// <summary>The optimizer, created on first use; creating the real stress tester opens a DX12 device, so call from a pool thread.</summary>
    public CurveOptimizer GetOptimizer()
    {
        lock (gate)
        {
            if (Gpu is null) throw new InvalidOperationException("No GPU is open.");
            tester ??= new ComputeStressTester();
            return optimizer ??= new CurveOptimizer(Gpu, tester, Store, Time);
        }
    }

    /// <summary>Replaces the stored result (null clears it) and notifies listeners.</summary>
    public void SetResult(OptimizationResult? result)
    {
        LastResult = result;
        ResultChanged?.Invoke();
    }

    /// <summary>
    /// Runs one cancellable operation at a time. The delegate decides where the work runs; the busy state,
    /// cancellation token and idle signal are managed here.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        if (IsBusy) throw new InvalidOperationException("An operation is already running.");
        var cts = new CancellationTokenSource();
        operationCts = cts;
        idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IsBusy = true;
        BusyChanged?.Invoke();
        try
        {
            return await work(cts.Token);
        }
        finally
        {
            operationCts = null;
            cts.Dispose();
            IsBusy = false;
            idle.TrySetResult();
            BusyChanged?.Invoke();
        }
    }

    /// <summary>Requests cancellation of the running operation, if any.</summary>
    public void CancelOperation()
    {
        try
        {
            operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Completes when no operation runs.</summary>
    public Task WaitIdleAsync() => idle is { Task.IsCompleted: false } ? idle.Task : Task.CompletedTask;

    public void Dispose()
    {
        CancelOperation();
        CloseGpu();
    }
}
