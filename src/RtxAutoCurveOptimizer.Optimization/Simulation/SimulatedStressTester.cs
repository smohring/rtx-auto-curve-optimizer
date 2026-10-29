using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization.Simulation;

/// <summary>Failure model and throughput scale of <see cref="SimulatedStressTester"/>.</summary>
public sealed class SimulatedStressOptions
{
    /// <summary>Failure rate per second and MHz above the true limit.</summary>
    public double HazardPerMhzPerSecond { get; init; } = 0.5;

    /// <summary>Stability limit reduction for pulsed load, in MHz.</summary>
    public double PulsedPenaltyMhz { get; init; } = 10;

    /// <summary>Stability limit reduction for variable load, in MHz.</summary>
    public double VariablePenaltyMhz { get; init; } = 15;

    /// <summary>Share of failures that are device losses instead of compute errors.</summary>
    public double DeviceLostFraction { get; init; } = 0.25;

    /// <summary>Throughput units per second per MHz at full duty.</summary>
    public double ThroughputPerMhz { get; init; } = 1000;

    /// <summary>Progress/clock step of a simulated run.</summary>
    public TimeSpan Step { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Stress tester on top of <see cref="SimulatedGpu"/>. A run advances the <see cref="SimulatedClock"/> instead of
/// waiting; failures follow an exponential model that rises with the distance above the true limit.
/// </summary>
public sealed class SimulatedStressTester : IStressTester
{
    private readonly SimulatedGpu gpu;
    private readonly SimulatedClock clock;
    private readonly SimulatedStressOptions options;
    private readonly Random random;
    private bool calibrated;

    public SimulatedStressTester(SimulatedGpu gpu, SimulatedClock clock, int seed = 1, SimulatedStressOptions? options = null)
    {
        this.gpu = gpu;
        this.clock = clock;
        this.options = options ?? new SimulatedStressOptions();
        random = new Random(seed);
    }

    public string AdapterName => "Simulated NVIDIA GPU";

    /// <summary>Number of calibrations.</summary>
    public int CalibrationCount { get; private set; }

    /// <summary>Largest distance (MHz) any run's operating point had above the true stable limit; the hang risk.</summary>
    public double MaxOvershootMhz { get; private set; } = double.NegativeInfinity;

    public Task CalibrateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        calibrated = true;
        CalibrationCount++;
        return Task.CompletedTask;
    }

    /// <summary>Runs on a pool thread so timer callbacks of the caller's sampler execute inline during clock advances.</summary>
    public Task<StressResult> RunAsync(StressRequest request, IProgress<StressProgress>? progress, CancellationToken ct)
    {
        if (!calibrated) throw new InvalidOperationException("The stress tester was used before calibration.");
        clock.EnterBusy();
        gpu.SetLoad(request.Pattern, request.Intensity);
        return Task.Run(() => RunCore(request, progress, ct), CancellationToken.None);
    }

    public void Dispose()
    {
    }

    private StressResult RunCore(StressRequest request, IProgress<StressProgress>? progress, CancellationToken ct)
    {
        try
        {
            SpinWait.SpinUntil(() => clock.PendingTimerCount > 0, TimeSpan.FromMilliseconds(200));

            var op = gpu.OperatingPoint(request.Pattern, request.Intensity);
            var duty = SimulatedGpu.Duty(request.Pattern, request.Intensity);
            var penalty = request.Pattern switch
            {
                LoadPattern.Pulsed => options.PulsedPenaltyMhz,
                LoadPattern.Variable => options.VariablePenaltyMhz,
                _ => 0,
            };
            var distanceMhz = (op.ClockKhz - (gpu.TrueLimitKhz(op.PointIndex) - penalty * 1000)) / 1000.0;
            MaxOvershootMhz = Math.Max(MaxOvershootMhz, (op.ClockKhz - gpu.TrueLimitKhz(op.PointIndex)) / 1000.0);
            var rate = options.HazardPerMhzPerSecond * Math.Max(0, distanceMhz) * Math.Max(duty, 0.1);
            var timeDraw = random.NextDouble();
            var kindDraw = random.NextDouble();
            var failAt = rate > 0 ? TimeSpan.FromSeconds(-Math.Log(1 - timeDraw) / rate) : TimeSpan.MaxValue;
            var throughput = options.ThroughputPerMhz * (op.ClockKhz / 1000.0) * duty;

            if (gpu.HasHeatModel) return RunHeated(request, progress, op, duty, penalty, throughput, ct);

            var end = failAt < request.Duration ? failAt : request.Duration;
            var elapsed = TimeSpan.Zero;
            while (elapsed < end)
            {
                if (ct.IsCancellationRequested)
                    return new StressResult(StressOutcome.Cancelled, elapsed, 0, throughput, null);

                var step = TimeSpan.FromTicks(Math.Min(options.Step.Ticks, (end - elapsed).Ticks));
                clock.Advance(step);
                elapsed += step;
                progress?.Report(new StressProgress(elapsed, throughput, 0));
            }

            return end == request.Duration
                ? new StressResult(StressOutcome.Passed, elapsed, 0, throughput, null)
                : Fail(elapsed, throughput, kindDraw);
        }
        finally
        {
            gpu.ClearLoad();
            clock.ExitBusy();
        }
    }

    /// <summary>
    /// Run under the heat model: each step the running clock (curve minus the heat-dependent derate) is compared
    /// with the heat-dependent limit, so a cold GPU at the full curve value can fail where a hot one passes.
    /// </summary>
    private StressResult RunHeated(
        StressRequest request, IProgress<StressProgress>? progress, SimulatedOperatingPoint op, double duty, double penalty,
        double throughput, CancellationToken ct)
    {
        var elapsed = TimeSpan.Zero;
        while (elapsed < request.Duration)
        {
            if (ct.IsCancellationRequested) return new StressResult(StressOutcome.Cancelled, elapsed, 0, throughput, null);
            var runningKhz = op.ClockKhz - gpu.CurrentDerateMhz * 1000;
            var limitKhz = gpu.CurrentLimitKhz(op.PointIndex);
            MaxOvershootMhz = Math.Max(MaxOvershootMhz, (runningKhz - limitKhz) / 1000.0);
            var distanceMhz = (runningKhz - (limitKhz - penalty * 1000)) / 1000.0;
            var step = TimeSpan.FromTicks(Math.Min(options.Step.Ticks, (request.Duration - elapsed).Ticks));
            var rate = options.HazardPerMhzPerSecond * Math.Max(0, distanceMhz) * Math.Max(duty, 0.1);
            var failed = random.NextDouble() < 1 - Math.Exp(-rate * step.TotalSeconds);
            clock.Advance(step);
            elapsed += step;
            progress?.Report(new StressProgress(elapsed, throughput, 0));
            if (failed) return Fail(elapsed, throughput, random.NextDouble());
        }
        return new StressResult(StressOutcome.Passed, elapsed, 0, throughput, null);
    }

    /// <summary>A failed run: a device loss (resets the GPU) or a compute error, chosen by <paramref name="kindDraw"/>.</summary>
    private StressResult Fail(TimeSpan elapsed, double throughput, double kindDraw)
    {
        var deviceLost = kindDraw < options.DeviceLostFraction;
        if (deviceLost) gpu.ResetDevice();
        calibrated &= !deviceLost;
        return deviceLost
            ? new StressResult(StressOutcome.DeviceLost, elapsed, 0, throughput, "Simulated device loss.")
            : new StressResult(StressOutcome.ComputeError, elapsed, 1 + (long)(kindDraw * 1000), throughput, "Simulated mismatch.");
    }
}
