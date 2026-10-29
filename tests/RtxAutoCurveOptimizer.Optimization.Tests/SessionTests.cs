using RtxAutoCurveOptimizer.Core;
using RtxAutoCurveOptimizer.Optimization.Simulation;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

/// <summary>Store decorator that remembers every saved snapshot in order.</summary>
internal sealed class RecordingStore(ISessionStore inner) : ISessionStore
{
    private readonly List<SessionState> snapshots = [];
    private readonly Lock gate = new();

    public IReadOnlyList<SessionState> Snapshots
    {
        get
        {
            lock (gate) return [.. snapshots];
        }
    }

    public SessionState? Latest
    {
        get
        {
            lock (gate) return snapshots.LastOrDefault();
        }
    }

    public void Save(SessionState state)
    {
        lock (gate) snapshots.Add(state);
        inner.Save(state);
    }

    public SessionState? Load(string id) => inner.Load(id);

    public IReadOnlyList<SessionState> List() => inner.List();
}

/// <summary>Tester decorator that records whether the in-flight marker was already stored when a run started.</summary>
internal sealed class InFlightObservingTester(IStressTester inner, RecordingStore store) : IStressTester
{
    public List<bool> MarkerPresent { get; } = [];

    public string AdapterName => inner.AdapterName;

    public Task CalibrateAsync(CancellationToken ct) => inner.CalibrateAsync(ct);

    public Task<StressResult> RunAsync(StressRequest request, IProgress<StressProgress>? progress, CancellationToken ct)
    {
        MarkerPresent.Add(store.Latest?.InFlight is not null);
        return inner.RunAsync(request, progress, ct);
    }

    public void Dispose() => inner.Dispose();
}

public sealed class SessionTests : IDisposable
{
    private const int Original = 50_000;
    private static readonly OptimizerSettings Quick = OptimizerSettings.For(OptimizationPreset.Quick);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rtxaco-session-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static SimulatedGpuOptions NonZeroOriginal => new() { InitialOffsetKhz = Original };

    [Fact]
    public async Task InFlightMarker_IsWrittenBeforeEveryTestAndClearedAfterwards()
    {
        var recording = new RecordingStore(new InMemorySessionStore());
        InFlightObservingTester? observer = null;
        var sim = Sim.Create(1, store: recording, wrapTester: t => observer = new InFlightObservingTester(t, recording));

        var result = await sim.Optimizer.RunAsync(Quick, null, CancellationToken.None);

        Assert.NotNull(observer);
        Assert.All(observer.MarkerPresent, present => Assert.True(present));
        Assert.True(observer.MarkerPresent.Count >= result.Tests.Count);

        var snapshots = recording.Snapshots;
        var markers = snapshots.Where(s => s.InFlight is not null).ToList();
        Assert.Equal(result.Tests.Count, markers.Count);
        for (var i = 0; i < snapshots.Count; i++)
        {
            if (snapshots[i].InFlight is null) continue;
            Assert.Null(snapshots[i + 1].InFlight);
            Assert.Equal(snapshots[i].Tests.Count + 1, snapshots[i + 1].Tests.Count);
        }
        Assert.Null(recording.Latest!.InFlight);
        Assert.Equal(SessionStatus.Completed, recording.Latest.Status);
    }

    [Fact]
    public async Task JsonStore_PersistsTheCompleteSessionAndRoundTrips()
    {
        var json = new JsonSessionStore(directory);
        var sim = Sim.Create(3, NonZeroOriginal, store: json);

        var result = await sim.Optimizer.RunAsync(Quick, null, CancellationToken.None);

        var path = Path.Combine(directory, result.SessionId + ".json");
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Search\"", text);
        Assert.Contains("\"Completed\"", text);

        var loaded = json.Load(result.SessionId);
        Assert.NotNull(loaded);
        Assert.Equal(result.Tests, loaded.Tests);
        Assert.Equal(result.Anchors, loaded.Anchors);
        Assert.Equal(result.Settings.AnchorVoltagesMv, loaded.Settings.AnchorVoltagesMv);
        Assert.Equal(result.Stock.Points, loaded.Stock.Points);
        Assert.Equal(result.OriginalOffsets.OffsetsKhz, loaded.OriginalOffsets.OffsetsKhz);
        Assert.Equal(result.StockReference, loaded.StockReference);
        Assert.Equal(result.Proposals.Count, loaded.Proposals.Count);
        Assert.Equal(result.Recommended!.Offsets.OffsetsKhz, loaded.Proposals.First(p => p.Kind == ProposalKind.Efficient).Offsets.OffsetsKhz);
        Assert.Equal(sim.Gpu.Identity, loaded.Gpu);
        Assert.Single(json.List());
    }

    [Fact]
    public void JsonStore_RejectsUnsafeIdsAndIgnoresCorruptFiles()
    {
        var json = new JsonSessionStore(directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{ not json");

        Assert.Empty(json.List());
        Assert.Null(json.Load("broken"));
        Assert.Null(json.Load("missing"));
        Assert.Throws<ArgumentException>(() => json.Load("..\\evil"));
    }

    [Fact]
    public async Task Recovery_MarksTheCrashedTestRestoresOffsetsAndResumeSkipsCompletedAnchors()
    {
        // Record a complete run, then take the snapshot written right before a search test of the third anchor.
        var recording = new RecordingStore(new InMemorySessionStore());
        var first = Sim.Create(7, NonZeroOriginal, store: recording);
        var full = await first.Optimizer.RunAsync(Quick, null, CancellationToken.None);
        var crash = recording.Snapshots.First(s =>
            s.InFlight is { Kind: TestKind.Search, AnchorIndex: 2 } && s.Anchors.Count == 2);
        var crashedFrequency = crash.InFlight!.FrequencyKhz;
        Assert.Equal(SessionStatus.InProgress, crash.Status);

        // A new process after the crash: the leftover test curve is still applied on the GPU.
        var json = new JsonSessionStore(directory);
        json.Save(crash);
        var second = Sim.Create(7, NonZeroOriginal, store: json);
        second.Gpu.ApplyOffsets(new CurveOffsets(Enumerable.Repeat(123_000, crash.Stock.Points.Count).ToList()));

        var recovery = new SessionRecovery(json, second.Clock);
        var found = recovery.FindUnfinished();
        Assert.NotNull(found);
        Assert.Equal(crash.Id, found.Id);

        var recovered = recovery.Recover(found, second.Gpu);

        Assert.Equal(RecoveryRestore.Restored, recovered.Restore);
        Assert.True(recovered.CrashedTestRecorded);
        Assert.All(second.Offsets.OffsetsKhz, o => Assert.Equal(Original, o));
        var crashed = recovered.Session.Tests.Single(t => t.Outcome == StressOutcome.Crashed);
        Assert.Equal(crashedFrequency, crashed.FrequencyKhz);
        Assert.False(crashed.Passed);
        Assert.Equal(SessionStatus.Interrupted, json.Load(crash.Id)!.Status);
        Assert.Null(json.Load(crash.Id)!.InFlight);
        Assert.Null(recovery.FindUnfinished());

        // Resume: completed anchors and references are reused, the crashed frequency counts as a failure.
        var events = new EventCollector();
        var resumed = await second.Optimizer.ResumeAsync(recovered.Session, events, CancellationToken.None);

        Assert.Equal(OptimizationStatus.Completed, resumed.Status);
        Assert.True(resumed.Anchors.Count == 4, string.Join("; ", events.Of<Warning>().Select(w => $"{w.Kind}:{w.Detail}"))
            + " | " + string.Join("; ", resumed.Tests.Where(t => t.AnchorIndex >= 2).Select(t => $"a{t.AnchorIndex} {t.Kind} {t.FrequencyKhz / 1000} {t.Outcome}")));
        Assert.Equal(crash.Anchors[0], resumed.Anchors[0]);
        Assert.Equal(crash.Anchors[1], resumed.Anchors[1]);
        Assert.Equal(crash.Tests.Count(t => t.AnchorIndex is 0 or 1), resumed.Tests.Count(t => t.AnchorIndex is 0 or 1));
        Assert.Equal(1, resumed.Tests.Count(t => t.Kind == TestKind.ReferenceStock));
        Assert.Contains(events.Of<Warning>(), w => w.Kind == WarningKind.ResumedFromSession);
        Assert.DoesNotContain(resumed.Tests, t => t.Kind == TestKind.Search && t.AnchorIndex == 2
                                                  && t.FrequencyKhz == crashedFrequency && t.Outcome != StressOutcome.Crashed);
        Assert.True(resumed.Anchors[2].MaxStableKhz < crashedFrequency);
        Assert.True(resumed.OffsetsRestored);
        Assert.All(second.Offsets.OffsetsKhz, o => Assert.Equal(Original, o));
        Assert.Equal(full.Anchors.Count, resumed.Anchors.Count);
        Assert.Equal(SessionStatus.Completed, json.Load(crash.Id)!.Status);
    }

    [Fact]
    public async Task Recovery_LeavesTheGpuAloneWhenTheSessionBelongsToAnotherGpu()
    {
        var recording = new RecordingStore(new InMemorySessionStore());
        var first = Sim.Create(7, NonZeroOriginal, store: recording);
        await first.Optimizer.RunAsync(Quick, null, CancellationToken.None);
        var crash = recording.Snapshots.First(s => s.InFlight is not null) with
        {
            Gpu = first.Gpu.Identity with { DeviceId = 0x1234 },
        };
        var json = new JsonSessionStore(directory);
        json.Save(crash);
        var second = Sim.Create(7, store: json);
        second.Gpu.ApplyOffsets(new CurveOffsets(Enumerable.Repeat(123_000, crash.Stock.Points.Count).ToList()));

        var recovered = new SessionRecovery(json, second.Clock).Recover(crash, second.Gpu);
        var resumed = await second.Optimizer.ResumeAsync(recovered.Session, null, CancellationToken.None);

        Assert.Equal(RecoveryRestore.GpuMismatch, recovered.Restore);
        Assert.All(second.Offsets.OffsetsKhz, o => Assert.Equal(123_000, o));
        Assert.Equal(OptimizationFailure.SessionMismatch, resumed.Failure);
        Assert.Empty(resumed.Anchors);
    }

    [Fact]
    public async Task FindUnfinished_IgnoresFinishedSessions()
    {
        var json = new JsonSessionStore(directory);
        var sim = Sim.Create(1, store: json);
        await sim.Optimizer.RunAsync(Quick, null, CancellationToken.None);

        Assert.Null(new SessionRecovery(json).FindUnfinished());
    }
}
