using System.Text.Json;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

public sealed class EventSerializationTests
{
    [Fact]
    public async Task EveryEventOfARun_SurvivesTheWorkerProtocol()
    {
        var sim = Sim.Create(3);
        var events = new EventCollector();
        await sim.Optimizer.RunAsync(OptimizerSettings.For(OptimizationPreset.Quick), events, CancellationToken.None);
        OptimizerEvent[] all = [.. events.Events, new RunFinished("20261027-120000")];

        foreach (var e in all)
        {
            var line = JsonSerializer.Serialize(e);
            Assert.DoesNotContain('\n', line);
            var back = JsonSerializer.Deserialize<OptimizerEvent>(line)!;
            Assert.Equal(e.GetType(), back.GetType());
            Assert.Equal(line, JsonSerializer.Serialize(back));
        }
        Assert.Contains(all, e => e is TestFinished);
        Assert.Contains(all, e => e is TelemetrySampled);
    }
}
