using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ComputeSharp;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Stress;

/// <summary>DX12 compute stress test (ComputeSharp) that verifies every dispatch against golden results kept on the GPU.</summary>
public sealed class ComputeStressTester : IStressTester
{
    private const int SeedSetCount = 2;
    private const uint SeedA = 0x1234ABCDu;
    private const uint SeedB = 0x9E3779B9u;
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(30);

    private readonly int _threadCount;
    private readonly TimeSpan _targetDispatch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ManualResetEventSlim _deviceLostSignal = new(false);
    private readonly int[] _counterHost = new int[2];

    private GraphicsDevice? _device;
    private bool _ownsDevice;
    private ReadWriteBuffer<uint>? _output;
    private ReadWriteBuffer<uint>?[] _golden = new ReadWriteBuffer<uint>?[SeedSetCount];
    private ReadWriteBuffer<int>? _counter;
    private uint[][]? _hostGolden;
    private string? _goldenAdapter;
    private int _iterations;
    private TimeSpan _dispatchTime;
    private string _deviceLostDetail = string.Empty;
    private bool _needsRecovery;
    private bool _disposed;

    /// <summary>Creates a tester on the NVIDIA adapter (or the default adapter if there is none).</summary>
    public ComputeStressTester() : this(1 << 22, TimeSpan.FromMilliseconds(50))
    {
    }

    internal ComputeStressTester(int threadCount, TimeSpan targetDispatch)
    {
        _threadCount = threadCount;
        _targetDispatch = targetDispatch;
        AcquireDevice();
        AdapterName = _device!.Name;
    }

    public string AdapterName { get; private set; }

    /// <summary>Kernel iterations per thread chosen at calibration (0 before calibration).</summary>
    public int Iterations => _iterations;

    /// <summary>Threads per dispatch.</summary>
    public int ThreadCount => _threadCount;

    /// <summary>Duration of one dispatch measured at the end of calibration.</summary>
    public TimeSpan CalibratedDispatchTime => _dispatchTime;

    /// <summary>True if the selected adapter is an NVIDIA GPU.</summary>
    public bool IsNvidia => AdapterName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true if a hardware NVIDIA adapter is present.</summary>
    public static bool NvidiaAdapterAvailable() => GraphicsDevice.EnumerateDevices()
        .Any(d => d.IsHardwareAccelerated && d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));

    public async Task CalibrateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
                EnsureDevice(linked.Token);
                CalibrateCore(linked.Token);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StressResult> RunAsync(StressRequest request, IProgress<StressProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RunCore(request, progress, ct), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        _gate.Wait(TimeSpan.FromSeconds(3));
        ReleaseDevice();
        _deviceLostSignal.Dispose();
    }

    /// <summary>Test hook: corrupts one value of the golden buffer on the GPU so the next run must report a mismatch.</summary>
    internal void InjectGoldenFault(int seedSet = 0, int index = 12345)
    {
        var buffer = _golden[seedSet] ?? throw new InvalidOperationException("Not calibrated.");
        uint[] patch = [~_hostGolden![seedSet][index]];
        buffer.CopyFrom(patch, 0, index, 1);
    }

    /// <summary>Test hook: behaves as if the device had been lost, so the next run re-acquires the device and re-uploads the goldens.</summary>
    internal void SimulateDeviceLoss() => _needsRecovery = true;

    private StressResult RunCore(StressRequest request, IProgress<StressProgress>? progress, CancellationToken userToken)
    {
        var sw = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, _disposeCts.Token);
        var ct = linked.Token;
        long threadIterations = 0;
        long dispatches = 0;
        var nextProgress = ProgressInterval;

        StressResult Finish(StressOutcome outcome, long mismatches, string? detail) =>
            new(outcome, sw.Elapsed, mismatches, Throughput(), detail);

        // Throughput excludes the clock ramp-up at the start (first 15 % of the run, at most 1 s) once that has passed.
        var warmup = TimeSpan.FromSeconds(Math.Min(1.0, request.Duration.TotalSeconds * 0.15));
        var warmedAt = TimeSpan.Zero;
        long iterationsAtWarm = 0;
        bool warmed = false;

        double Throughput()
        {
            var span = sw.Elapsed - warmedAt;
            return span.TotalSeconds > 0 ? (threadIterations - iterationsAtWarm) / span.TotalSeconds : 0;
        }

        void ReportIfDue(long mismatches)
        {
            if (sw.Elapsed < nextProgress)
            {
                return;
            }

            progress?.Report(new StressProgress(sw.Elapsed, Throughput(), mismatches));
            while (nextProgress <= sw.Elapsed)
            {
                nextProgress += ProgressInterval;
            }
        }

        // Sleeps until the stopwatch reaches 'until'; returns false if cancelled.
        bool Idle(TimeSpan until)
        {
            while (true)
            {
                var remaining = until - sw.Elapsed;
                ReportIfDue(0);
                if (remaining <= TimeSpan.Zero)
                {
                    return !ct.IsCancellationRequested;
                }

                if (ct.WaitHandle.WaitOne(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50)))
                {
                    return false;
                }
            }
        }

        try
        {
            EnsureDevice(ct);
            if (_hostGolden is null)
            {
                CalibrateCore(ct);
                sw.Restart();
            }

            var schedule = new LoadSchedule(request.Pattern, request.Intensity, Environment.TickCount);
            var phaseEnd = TimeSpan.Zero;
            double duty = 1;
            double loadMs = 0;
            var phaseStart = TimeSpan.Zero;

            // Runs one dispatch; returns a failure result on mismatch, null otherwise.
            StressResult? Dispatch()
            {
                int seedSet = (int)(dispatches & 1);
                var (mismatches, lastIndex, time) = RunOnce(seedSet, _iterations);
                dispatches++;
                threadIterations += (long)_threadCount * _iterations;
                if (!warmed && sw.Elapsed >= warmup)
                {
                    warmed = true;
                    warmedAt = sw.Elapsed;
                    iterationsAtWarm = threadIterations;
                }

                loadMs += time.TotalMilliseconds;
                if (mismatches > 0)
                {
                    progress?.Report(new StressProgress(sw.Elapsed, Throughput(), mismatches));
                    return Finish(StressOutcome.ComputeError, mismatches,
                        $"{mismatches} mismatching values in seed set {(seedSet == 0 ? "A" : "B")}, last at index {lastIndex - 1}, dispatch {dispatches}");
                }

                ReportIfDue(0);
                return null;
            }

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    return Finish(StressOutcome.Cancelled, 0, null);
                }

                var now = sw.Elapsed;
                if (now >= request.Duration)
                {
                    break;
                }

                if (now >= phaseEnd)
                {
                    var phase = schedule.Next();
                    duty = phase.Duty;
                    phaseEnd = now + phase.Length;
                    phaseStart = now;
                    loadMs = 0;
                }

                var limit = phaseEnd < request.Duration ? phaseEnd : request.Duration;
                if (duty <= 0)
                {
                    if (!Idle(limit))
                    {
                        return Finish(StressOutcome.Cancelled, 0, null);
                    }

                    continue;
                }

                if (duty >= 0.999)
                {
                    var failure = Dispatch();
                    if (failure is not null)
                    {
                        return failure;
                    }

                    continue;
                }

                // Partial duty: dispatch while load time is below duty * elapsed phase time, then idle to the end of the 100 ms window.
                var windowEnd = now + Window;
                while ((loadMs == 0 || loadMs < duty * (sw.Elapsed - phaseStart).TotalMilliseconds)
                       && sw.Elapsed < limit && !ct.IsCancellationRequested)
                {
                    var failure = Dispatch();
                    if (failure is not null)
                    {
                        return failure;
                    }
                }

                if (!Idle(windowEnd < limit ? windowEnd : limit))
                {
                    return Finish(StressOutcome.Cancelled, 0, null);
                }
            }

            progress?.Report(new StressProgress(sw.Elapsed, Throughput(), 0));
            return Finish(StressOutcome.Passed, 0, null);
        }
        catch (OperationCanceledException)
        {
            return Finish(StressOutcome.Cancelled, 0, null);
        }
        catch (Exception ex)
        {
            return ClassifyFailure(ex, Finish);
        }
    }

    // Maps an exception from dispatch/readback/recovery to DeviceLost (D3D/COM or lost device) or Crashed.
    private StressResult ClassifyFailure(Exception ex, Func<StressOutcome, long, string?, StressResult> finish)
    {
        bool lost = _deviceLostSignal.Wait(300) || IsDeviceException(ex);
        if (lost)
        {
            _needsRecovery = true;
            string reason = _deviceLostDetail.Length > 0 ? $"{_deviceLostDetail}; " : string.Empty;
            return finish(StressOutcome.DeviceLost, 0, $"{reason}{ex.GetType().Name}: {ex.Message}");
        }

        return finish(StressOutcome.Crashed, 0, $"{ex.GetType().Name}: {ex.Message}");
    }

    private static bool IsDeviceException(Exception ex) =>
        ex is COMException or Win32Exception
        || ex.InnerException is COMException
        || ex.GetType().Name.Contains("DeviceLost", StringComparison.OrdinalIgnoreCase)
        || (ex.Message.Contains("device", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("lost", StringComparison.OrdinalIgnoreCase));

    private void CalibrateCore(CancellationToken ct)
    {
        var device = _device!;

        // Find an iteration count whose dispatch takes about the target time.
        int iterations = 128;
        TimeSpan time;
        do
        {
            ct.ThrowIfCancellationRequested();
            iterations *= 2;
            time = TimeKernel(iterations);
        }
        while (time < TimeSpan.FromMilliseconds(8) && iterations < (1 << 22));

        iterations = Scale(iterations, time);

        // Warm up so clocks ramp before the final measurement.
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed < TimeSpan.FromSeconds(1))
        {
            ct.ThrowIfCancellationRequested();
            TimeKernel(iterations);
        }

        iterations = Scale(iterations, MedianKernelTime(iterations, 5, ct));
        _iterations = iterations;
        _dispatchTime = MedianKernelTime(iterations, 5, ct);

        // Golden results per seed set; each is computed twice and must agree.
        var host = new uint[SeedSetCount][];
        for (int set = 0; set < SeedSetCount; set++)
        {
            ct.ThrowIfCancellationRequested();
            device.For(_threadCount, new StressKernel(_output!, SeedFor(set), iterations));
            _output!.CopyTo(_golden[set]!);
            var (mismatches, _, _) = RunOnce(set, iterations);
            if (mismatches != 0)
            {
                throw new InvalidOperationException(
                    $"GPU gave inconsistent results during calibration (seed set {set}, {mismatches} mismatches); the GPU is already unstable.");
            }

            host[set] = _golden[set]!.ToArray();
        }

        _hostGolden = host;
        _goldenAdapter = AdapterName;
    }

    private int Scale(int iterations, TimeSpan measured)
    {
        double factor = _targetDispatch.TotalMilliseconds / Math.Max(measured.TotalMilliseconds, 0.01);
        long scaled = (long)(iterations * factor);
        return (int)Math.Clamp(scaled / 8 * 8, 64, 1 << 24);
    }

    private TimeSpan MedianKernelTime(int iterations, int count, CancellationToken ct)
    {
        var times = new TimeSpan[count];
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            times[i] = TimeKernel(iterations);
        }

        Array.Sort(times);
        return times[count / 2];
    }

    private TimeSpan TimeKernel(int iterations)
    {
        var sw = Stopwatch.StartNew();
        _device!.For(_threadCount, new StressKernel(_output!, SeedFor(0), iterations));
        return sw.Elapsed;
    }

    private static uint SeedFor(int set) => set == 0 ? SeedA : SeedB;

    // One kernel dispatch plus verification; returns mismatch count, last mismatching index + 1 and the wall time.
    private (long Mismatches, int LastIndexPlusOne, TimeSpan Time) RunOnce(int seedSet, int iterations)
    {
        var device = _device!;
        var sw = Stopwatch.StartNew();
        using (var context = device.CreateComputeContext())
        {
            context.Clear(_counter!);
            context.Barrier(_counter!);
            context.For(_threadCount, new StressKernel(_output!, SeedFor(seedSet), iterations));
            context.Barrier(_output!);
            context.For(_threadCount, new VerifyKernel(_output!, _golden[seedSet]!, _counter!));
        }

        _counter!.CopyTo(_counterHost);
        return (_counterHost[0], _counterHost[1], sw.Elapsed);
    }

    // Makes sure a live device with buffers exists; after a device loss re-acquires one for up to 30 s.
    private void EnsureDevice(CancellationToken ct)
    {
        if (_device is not null && !_needsRecovery && !_deviceLostSignal.IsSet)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        Exception? last = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ReleaseDevice();
                AcquireDevice();
                AdapterName = _device!.Name;
                RestoreGolden();
                _needsRecovery = false;
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }

            if (sw.Elapsed > RecoveryTimeout)
            {
                ReleaseDevice();
                _needsRecovery = true;
                throw new InvalidOperationException($"Could not re-acquire a GPU device within {RecoveryTimeout.TotalSeconds:0} s.", last);
            }

            ct.WaitHandle.WaitOne(1000);
        }
    }

    // Uploads the host copy of the golden results to the GPU buffers; if the adapter changed, they are dropped for recalibration.
    internal void RestoreGolden()
    {
        if (_hostGolden is null)
        {
            return;
        }

        if (_goldenAdapter != AdapterName)
        {
            _hostGolden = null;
            return;
        }

        for (int set = 0; set < SeedSetCount; set++)
        {
            _golden[set]!.CopyFrom(_hostGolden[set]);
        }
    }

    // Creates the device (NVIDIA preferred) and all buffers, and warms up both shaders.
    private void AcquireDevice()
    {
        GraphicsDevice? device = null;
        foreach (var candidate in GraphicsDevice.QueryDevices(info =>
                     info.IsHardwareAccelerated && info.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
        {
            if (device is null)
            {
                device = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }

        _ownsDevice = device is not null;
        device ??= GraphicsDevice.GetDefault();
        _device = device;
        _deviceLostSignal.Reset();
        _deviceLostDetail = string.Empty;
        device.DeviceLost += OnDeviceLost;

        _output = device.AllocateReadWriteBuffer<uint>(_threadCount);
        for (int set = 0; set < SeedSetCount; set++)
        {
            _golden[set] = device.AllocateReadWriteBuffer<uint>(_threadCount);
        }

        _counter = device.AllocateReadWriteBuffer<int>(2);
        RunOnce(0, 8);
    }

    private void ReleaseDevice()
    {
        var device = _device;
        if (device is not null)
        {
            device.DeviceLost -= OnDeviceLost;
        }

        TryDispose(_output);
        TryDispose(_golden[0]);
        TryDispose(_golden[1]);
        TryDispose(_counter);
        _output = null;
        _golden = new ReadWriteBuffer<uint>?[SeedSetCount];
        _counter = null;
        if (_ownsDevice)
        {
            TryDispose(device);
        }

        _device = null;
    }

    private static void TryDispose(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
        {
            // The device is already gone; nothing left to release.
        }
    }

    private void OnDeviceLost(object? sender, DeviceLostEventArgs e)
    {
        _deviceLostDetail = $"Device lost ({e.Reason})";
        _deviceLostSignal.Set();
    }
}
