using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.App.Services;

/// <summary>
/// Wraps a stress tester running on virtual time and slows every progress report down so the UI can follow
/// a simulated run in near real time (speed = virtual seconds per real second).
/// </summary>
internal sealed class PacedStressTester(IStressTester inner, double speed) : IStressTester
{
    public string AdapterName => inner.AdapterName;

    public Task CalibrateAsync(CancellationToken ct) => inner.CalibrateAsync(ct);

    public Task<StressResult> RunAsync(StressRequest request, IProgress<StressProgress>? progress, CancellationToken ct) =>
        inner.RunAsync(request, new PacedProgress(progress, speed), ct);

    public void Dispose() => inner.Dispose();

    private sealed class PacedProgress(IProgress<StressProgress>? target, double speed) : IProgress<StressProgress>
    {
        private TimeSpan last;

        public void Report(StressProgress value)
        {
            target?.Report(value);
            var step = value.Elapsed - last;
            last = value.Elapsed;
            if (step > TimeSpan.Zero) Thread.Sleep(step / speed);
        }
    }
}
