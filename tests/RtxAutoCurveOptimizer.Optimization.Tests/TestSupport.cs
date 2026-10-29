using System.Text;
using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization.Simulation;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

/// <summary>Thread-safe event sink with an optional hook that runs for every event.</summary>
internal sealed class EventCollector : IProgress<OptimizerEvent>
{
    private readonly List<OptimizerEvent> events = [];
    private readonly Lock gate = new();

    public Action<OptimizerEvent>? Hook { get; set; }

    public void Report(OptimizerEvent value)
    {
        lock (gate) events.Add(value);
        Hook?.Invoke(value);
    }

    public IReadOnlyList<OptimizerEvent> Events
    {
        get
        {
            lock (gate) return [.. events];
        }
    }

    public IEnumerable<T> Of<T>() where T : OptimizerEvent => Events.OfType<T>();
}

/// <summary>A simulated clock, GPU, stress tester and optimizer wired together.</summary>
internal sealed class Sim
{
    public required SimulatedClock Clock { get; init; }
    public required SimulatedGpu Gpu { get; init; }
    public required SimulatedStressTester Tester { get; init; }
    public required ISessionStore Store { get; init; }
    public required CurveOptimizer Optimizer { get; init; }

    public static Sim Create(
        int seed = 1, SimulatedGpuOptions? gpuOptions = null, SimulatedStressOptions? stressOptions = null,
        ISessionStore? store = null, Func<SimulatedStressTester, IStressTester>? wrapTester = null)
    {
        var clock = new SimulatedClock();
        var gpu = new SimulatedGpu(clock, gpuOptions, seed);
        var tester = new SimulatedStressTester(gpu, clock, seed, stressOptions);
        store ??= new InMemorySessionStore();
        var optimizer = new CurveOptimizer(gpu, wrapTester?.Invoke(tester) ?? tester, store, clock);
        return new Sim { Clock = clock, Gpu = gpu, Tester = tester, Store = store, Optimizer = optimizer };
    }

    public CurveOffsets Offsets => Gpu.ReadOffsets();
}

internal static class TestFormat
{
    public static string AnchorTable(OptimizationResult result, SimulatedGpu gpu)
    {
        var sb = new StringBuilder();
        sb.AppendLine("anchor  mV    stock  maxStable  recommended  trueLimit  power[W]  clock  perf%   perf/W   limited intensity");
        foreach (var a in result.Anchors)
        {
            sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{a.AnchorIndex,5}  {a.VoltageUv / 1000,4}  {a.StockKhz / 1000,5}  {a.MaxStableKhz / 1000,9}  {a.RecommendedKhz / 1000,11}  {gpu.TrueLimitKhz(a.PointIndex) / 1000,9}  {a.AvgPowerW,8:F1}  {a.AvgClockMhz,5:F0}  {a.ThroughputRelative * 100,5:F1}  {a.PerfPerWatt,7:F2}  {a.Limited,-7} {a.Intensity:F1}"));
        }
        return sb.ToString();
    }

    public static string Summary(OptimizationResult result)
    {
        var tests = result.Tests;
        return $"status={result.Status} tests={tests.Count} search={tests.Count(t => t.Kind == TestKind.Search)} " +
               $"confirm={tests.Count(t => t.Kind == TestKind.Confirm)} " +
               $"failed={tests.Count(t => t.Outcome != StressOutcome.Passed)} elapsed(virtual)={result.Elapsed}";
    }
}
