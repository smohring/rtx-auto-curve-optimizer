using System.Diagnostics;
using System.Globalization;
using RtxAutoCurveOptimizer.Core;
using Xunit.Abstractions;

namespace RtxAutoCurveOptimizer.Stress.Tests;

/// <summary>Creates and calibrates one tester shared by all GPU tests (calibration takes several seconds).</summary>
public sealed class GpuFixture : IAsyncLifetime
{
    public ComputeStressTester? Tester { get; private set; }

    public async Task InitializeAsync()
    {
        if (!ComputeStressTester.NvidiaAdapterAvailable())
        {
            return;
        }

        Tester = new ComputeStressTester();
        await Tester.CalibrateAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Tester?.Dispose();
        return Task.CompletedTask;
    }
}

[Trait("Category", "Gpu")]
public class GpuStressTests(GpuFixture fixture, ITestOutputHelper output) : IClassFixture<GpuFixture>
{
    private ComputeStressTester Tester => fixture.Tester!;

    private static StressRequest Request(double seconds, LoadPattern pattern = LoadPattern.Steady, double intensity = 1.0) =>
        new(TimeSpan.FromSeconds(seconds), pattern, intensity);

    // Dispatch time implied by a run's throughput (thread-iterations per second).
    private double DispatchMs(double throughput) =>
        (double)Tester.ThreadCount * Tester.Iterations / throughput * 1000.0;

    [GpuFact]
    public void Calibration_TargetsDispatchTimeWindow()
    {
        output.WriteLine($"Adapter: {Tester.AdapterName}, threads {Tester.ThreadCount}, iterations {Tester.Iterations}, calibrated dispatch {Tester.CalibratedDispatchTime.TotalMilliseconds:F1} ms");

        Assert.True(Tester.IsNvidia);
        Assert.InRange(Tester.CalibratedDispatchTime.TotalMilliseconds, 30, 100);
    }

    [GpuFact]
    public async Task Steady_PassesWithoutMismatches_ReportsProgressAndStableThroughput()
    {
        var reports = new List<StressProgress>();
        var progress = new SyncProgress<StressProgress>(reports.Add);

        var first = await Tester.RunAsync(Request(5), progress, CancellationToken.None);
        var second = await Tester.RunAsync(Request(5), null, CancellationToken.None);

        double deviation = Math.Abs(first.Throughput - second.Throughput) / first.Throughput;
        output.WriteLine($"Run 1: {first.Outcome}, {first.Elapsed.TotalSeconds:F2} s, {first.Throughput:E4} thread-iter/s, dispatch {DispatchMs(first.Throughput):F1} ms");
        output.WriteLine($"Run 2: {second.Outcome}, {second.Elapsed.TotalSeconds:F2} s, {second.Throughput:E4} thread-iter/s, dispatch {DispatchMs(second.Throughput):F1} ms");
        output.WriteLine($"Throughput deviation {deviation:P2}, progress reports {reports.Count}");

        Assert.Equal(StressOutcome.Passed, first.Outcome);
        Assert.Equal(0, first.MismatchCount);
        Assert.Equal(StressOutcome.Passed, second.Outcome);
        Assert.Equal(0, second.MismatchCount);
        Assert.InRange(DispatchMs(first.Throughput), 30, 100);
        Assert.InRange(deviation, 0, 0.05);
        Assert.True(reports.Count >= 15, $"expected about 20 progress reports, got {reports.Count}");
        Assert.True(MaxGap(reports) <= TimeSpan.FromMilliseconds(450), $"progress gap {MaxGap(reports).TotalMilliseconds:F0} ms");
    }

    [GpuFact]
    public async Task Pulsed_Runs()
    {
        var result = await Tester.RunAsync(Request(5, LoadPattern.Pulsed), null, CancellationToken.None);

        output.WriteLine($"Pulsed: {result.Outcome}, {result.Elapsed.TotalSeconds:F2} s, {result.Throughput:E4} thread-iter/s");
        Assert.Equal(StressOutcome.Passed, result.Outcome);
        Assert.Equal(0, result.MismatchCount);
        Assert.InRange(result.Elapsed.TotalSeconds, 5.0, 5.3);
    }

    [GpuFact]
    public async Task Variable_Runs()
    {
        var result = await Tester.RunAsync(Request(8, LoadPattern.Variable), null, CancellationToken.None);

        output.WriteLine($"Variable: {result.Outcome}, {result.Elapsed.TotalSeconds:F2} s, {result.Throughput:E4} thread-iter/s");
        Assert.Equal(StressOutcome.Passed, result.Outcome);
        Assert.Equal(0, result.MismatchCount);
    }

    [GpuFact]
    public async Task HalfIntensity_ReducesThroughputRoughlyByHalf()
    {
        var full = await Tester.RunAsync(Request(3), null, CancellationToken.None);
        var half = await Tester.RunAsync(Request(4, LoadPattern.Steady, 0.5), null, CancellationToken.None);

        double ratio = half.Throughput / full.Throughput;
        output.WriteLine($"Intensity 0.5 / 1.0 throughput ratio: {ratio:F3}");
        Assert.Equal(StressOutcome.Passed, half.Outcome);
        Assert.InRange(ratio, 0.35, 0.65);
    }

    [GpuFact]
    public async Task Cancellation_ReturnsWithin200Ms()
    {
        foreach (var pattern in new[] { LoadPattern.Steady, LoadPattern.Pulsed })
        {
            using var cts = new CancellationTokenSource();
            var task = Tester.RunAsync(Request(30, pattern), null, cts.Token);
            await Task.Delay(1500);

            var sw = Stopwatch.StartNew();
            cts.Cancel();
            var result = await task;
            sw.Stop();

            output.WriteLine($"{pattern}: cancellation latency {sw.Elapsed.TotalMilliseconds:F0} ms, outcome {result.Outcome}");
            Assert.Equal(StressOutcome.Cancelled, result.Outcome);
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(200), $"latency {sw.Elapsed.TotalMilliseconds:F0} ms");
        }
    }

    [GpuFact]
    public async Task InjectedGoldenCorruption_YieldsComputeErrorFast()
    {
        Tester.InjectGoldenFault(seedSet: 0);
        try
        {
            var result = await Tester.RunAsync(Request(10), null, CancellationToken.None);

            output.WriteLine($"Fault: {result.Outcome} after {result.Elapsed.TotalMilliseconds:F0} ms, mismatches {result.MismatchCount}, detail: {result.Detail}");
            Assert.Equal(StressOutcome.ComputeError, result.Outcome);
            Assert.True(result.MismatchCount >= 1);
            Assert.True(result.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Contains("seed set", result.Detail);
        }
        finally
        {
            Tester.RestoreGolden();
        }

        var clean = await Tester.RunAsync(Request(1), null, CancellationToken.None);
        Assert.Equal(StressOutcome.Passed, clean.Outcome);
    }

    [GpuFact]
    public async Task AfterDeviceLoss_DeviceAndGoldensAreRestoredFromHostCopy()
    {
        Tester.InjectGoldenFault(seedSet: 1);
        Tester.SimulateDeviceLoss();

        var result = await Tester.RunAsync(Request(2), null, CancellationToken.None);

        output.WriteLine($"After simulated loss: {result.Outcome}, {result.Elapsed.TotalSeconds:F2} s, mismatches {result.MismatchCount}");
        Assert.Equal(StressOutcome.Passed, result.Outcome);
        Assert.Equal(0, result.MismatchCount);
    }

    [GpuFact]
    public async Task SteadyLoad_DrivesGpuUtilizationAndPower()
    {
        var baseline = await Sample(TimeSpan.FromSeconds(2));
        var sampler = Sample(TimeSpan.FromSeconds(11));
        await Task.Delay(500);
        var result = await Tester.RunAsync(Request(10), null, CancellationToken.None);
        var load = await sampler;

        output.WriteLine($"Baseline ({baseline.Count} samples): util {Avg(baseline, s => s.Util):F0} %, power {Avg(baseline, s => s.Power):F0} W, clock {Avg(baseline, s => s.Clock):F0} MHz");
        var steady = load.Skip(2).Take(load.Count - 4).ToList();
        output.WriteLine($"Under load ({steady.Count} samples): util {Avg(steady, s => s.Util):F0} % (min {steady.Min(s => s.Util)}), power {Avg(steady, s => s.Power):F0} W (max {steady.Max(s => s.Power):F0}), clock {Avg(steady, s => s.Clock):F0} MHz");

        Assert.Equal(StressOutcome.Passed, result.Outcome);
        Assert.True(Avg(steady, s => s.Util) >= 90, "GPU utilization under load is too low");
    }

    private static double Avg(List<GpuSample> samples, Func<GpuSample, double> select) =>
        samples.Count == 0 ? double.NaN : samples.Average(select);

    private static TimeSpan MaxGap(List<StressProgress> reports)
    {
        var max = TimeSpan.Zero;
        for (int i = 1; i < reports.Count; i++)
        {
            var gap = reports[i].Elapsed - reports[i - 1].Elapsed;
            if (gap > max)
            {
                max = gap;
            }
        }

        return max;
    }

    private readonly record struct GpuSample(double Util, double Power, double Clock);

    // Samples nvidia-smi every 500 ms for the given duration.
    private static async Task<List<GpuSample>> Sample(TimeSpan duration)
    {
        var info = new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu,power.draw,clocks.gr --format=csv,noheader,nounits -lms 500")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info)!;
        var samples = new List<GpuSample>();
        var reader = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 3
                    && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var util)
                    && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var power)
                    && double.TryParse(parts[2], CultureInfo.InvariantCulture, out var clock))
                {
                    lock (samples)
                    {
                        samples.Add(new GpuSample(util, power, clock));
                    }
                }
            }
        });

        await Task.Delay(duration);
        process.Kill();
        await reader;
        lock (samples)
        {
            return [.. samples];
        }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (this)
            {
                handler(value);
            }
        }
    }
}
