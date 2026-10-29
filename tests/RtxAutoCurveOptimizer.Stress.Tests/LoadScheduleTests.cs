using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Stress.Tests;

public class LoadScheduleTests
{
    [Fact]
    public void Steady_IsOneLongPhaseAtIntensity()
    {
        var schedule = new LoadSchedule(LoadPattern.Steady, 0.7, 1);

        var phase = schedule.Next();

        Assert.Equal(0.7, phase.Duty);
        Assert.True(phase.Length >= TimeSpan.FromMinutes(10));
    }

    [Theory]
    [InlineData(0.0, 0.1)]
    [InlineData(5.0, 1.0)]
    public void Intensity_IsClampedToValidRange(double requested, double expected)
    {
        var schedule = new LoadSchedule(LoadPattern.Steady, requested, 1);

        Assert.Equal(expected, schedule.Next().Duty);
    }

    [Fact]
    public void Pulsed_AlternatesLoadAndIdleWithinJitter()
    {
        var schedule = new LoadSchedule(LoadPattern.Pulsed, 1.0, 42);

        for (int i = 0; i < 50; i++)
        {
            var load = schedule.Next();
            var idle = schedule.Next();

            Assert.Equal(1.0, load.Duty);
            Assert.InRange(load.Length.TotalMilliseconds, 280, 520);
            Assert.Equal(0.0, idle.Duty);
            Assert.InRange(idle.Length.TotalMilliseconds, 70, 130);
        }
    }

    [Fact]
    public void Variable_MixesSteadyPulsesAndPartialDuty()
    {
        var schedule = new LoadSchedule(LoadPattern.Variable, 1.0, 7);

        var phases = Enumerable.Range(0, 200).Select(_ => schedule.Next()).ToList();

        Assert.Contains(phases, p => p.Duty == 0.0);
        Assert.Contains(phases, p => p.Duty == 1.0 && p.Length >= TimeSpan.FromMilliseconds(1500));
        Assert.Contains(phases, p => p.Duty is > 0.0 and < 0.9);
        Assert.All(phases, p => Assert.InRange(p.Duty, 0.0, 1.0));
    }

    [Fact]
    public void SameSeed_GivesSameSequence()
    {
        var a = new LoadSchedule(LoadPattern.Variable, 0.8, 5);
        var b = new LoadSchedule(LoadPattern.Variable, 0.8, 5);

        for (int i = 0; i < 30; i++)
        {
            Assert.Equal(a.Next(), b.Next());
        }
    }
}
