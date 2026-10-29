namespace RtxAutoCurveOptimizer.Core;

public enum LoadPattern { Steady, Pulsed, Variable }

/// <summary>One stress run; Intensity (0.1-1.0) is the duty cycle within each 100 ms window.</summary>
public sealed record StressRequest(TimeSpan Duration, LoadPattern Pattern, double Intensity);

public enum StressOutcome { Passed, ComputeError, DeviceLost, Cancelled, Crashed }

/// <summary>Result of a stress run; throughput is in work units per second and only comparable between runs of the same tester.</summary>
public sealed record StressResult(
    StressOutcome Outcome, TimeSpan Elapsed, long MismatchCount,
    double Throughput, string? Detail);

public readonly record struct StressProgress(TimeSpan Elapsed, double Throughput, long MismatchCount);

/// <summary>GPU stress test that detects computation errors and device loss.</summary>
public interface IStressTester : IDisposable
{
    string AdapterName { get; }

    /// <summary>Computes the golden reference results; call once with the GPU at stock settings.</summary>
    Task CalibrateAsync(CancellationToken ct);

    Task<StressResult> RunAsync(StressRequest request, IProgress<StressProgress>? progress, CancellationToken ct);
}
