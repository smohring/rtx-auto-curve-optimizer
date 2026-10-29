namespace RtxAutoCurveOptimizer.Optimization.Tests;

public sealed class WatchdogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 21, 0, 0, TimeSpan.Zero);

    private static Heartbeat Beat(TimeSpan sinceProgress, int restarts = 0, bool finished = false) =>
        new(1234, Now - sinceProgress, finished, restarts, ["--lang", "de-DE"]);

    [Fact]
    public void NoHeartbeatOrFinishedRun_Exits()
    {
        Assert.Equal(WatchdogAction.Exit, WatchdogPolicy.Decide(null, false, Now));
        Assert.Equal(WatchdogAction.Exit, WatchdogPolicy.Decide(Beat(TimeSpan.FromMinutes(10), finished: true), false, Now));
    }

    [Fact]
    public void AliveWithRecentProgress_Waits() =>
        Assert.Equal(WatchdogAction.Wait, WatchdogPolicy.Decide(Beat(TimeSpan.FromSeconds(30)), true, Now));

    [Fact]
    public void ProcessDied_IsRestarted() =>
        Assert.Equal(WatchdogAction.Restart, WatchdogPolicy.Decide(Beat(TimeSpan.FromSeconds(1)), false, Now));

    [Fact]
    public void AliveButNoProgressPastTheTimeout_IsKilledAndRestarted() =>
        Assert.Equal(WatchdogAction.KillAndRestart,
            WatchdogPolicy.Decide(Beat(WatchdogPolicy.HangTimeout + TimeSpan.FromSeconds(1)), true, Now));

    [Fact]
    public void RestartBudgetUsedUp_StopsRestarting()
    {
        var hung = Beat(WatchdogPolicy.HangTimeout + TimeSpan.FromSeconds(1), WatchdogPolicy.MaxRestarts);
        Assert.Equal(WatchdogAction.Kill, WatchdogPolicy.Decide(hung, true, Now));
        Assert.Equal(WatchdogAction.GiveUp, WatchdogPolicy.Decide(hung, false, Now));
    }

    [Fact]
    public void RestartThatNeverWritesAHeartbeat_UsesUpTheBudgetWithGrowingWaitsInsteadOfEndingTheWatchdog()
    {
        var beat = Beat(TimeSpan.FromSeconds(1));
        var waits = new List<TimeSpan>();
        while (WatchdogPolicy.Decide(beat, false, Now) == WatchdogAction.Restart)
        {
            waits.Add(WatchdogPolicy.RecoveryWait(beat.Restarts));
            beat = WatchdogPolicy.AfterFailedStart(beat);
        }

        Assert.Equal(WatchdogPolicy.MaxRestarts, waits.Count);
        Assert.True(waits.Zip(waits.Skip(1)).All(w => w.Second > w.First));
        Assert.Equal(WatchdogAction.GiveUp, WatchdogPolicy.Decide(beat, false, Now));
    }

    [Fact]
    public void HeartbeatFile_RoundTripsAndToleratesGarbage()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var file = new HeartbeatFile(Path.Combine(dir.FullName, "heartbeat.json"));
            Assert.Null(file.Read());

            var beat = Beat(TimeSpan.FromSeconds(5), 2);
            file.Write(beat);
            var read = file.Read();
            Assert.NotNull(read);
            Assert.Equal((beat.ProcessId, beat.LastProgress, beat.Finished, beat.Restarts), (read.ProcessId, read.LastProgress, read.Finished, read.Restarts));
            Assert.Equal(beat.RestartArguments, read.RestartArguments);

            File.WriteAllText(file.FilePath, "{ not json");
            Assert.Null(file.Read());
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
