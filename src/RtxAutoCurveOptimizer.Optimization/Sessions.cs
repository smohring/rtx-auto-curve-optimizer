using System.Text.Json;
using System.Text.Json.Serialization;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>InProgress = running or crashed; Interrupted = recovered after a crash and resumable.</summary>
public enum SessionStatus { InProgress, Interrupted, Completed, Cancelled, Failed }

/// <summary>Complete persisted state of one optimization run.</summary>
public sealed record SessionState(
    string Id, SessionStatus Status, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
    GpuIdentity Gpu, StockCurve Stock, CurveOffsets OriginalOffsets, OptimizerSettings Settings,
    ReferenceMeasurement? StockReference, ReferenceMeasurement? CurrentReference,
    IReadOnlyList<TestRecord> Tests, IReadOnlyList<AnchorResult> Anchors,
    IReadOnlyList<ProfileProposal> Proposals, InFlightTest? InFlight,
    bool OffsetsRestored, OptimizationFailure Failure, string? FailureDetail)
{
    /// <summary>Converts the stored state to a result (status mapped from <see cref="Status"/>).</summary>
    public OptimizationResult ToResult() => new(
        Status switch
        {
            SessionStatus.Completed => OptimizationStatus.Completed,
            SessionStatus.Failed => OptimizationStatus.Failed,
            _ when Failure != OptimizationFailure.None => OptimizationStatus.Failed,
            _ => OptimizationStatus.Cancelled,
        },
        Failure, FailureDetail, Id, Settings, Stock, OriginalOffsets, StockReference, CurrentReference,
        Anchors, Proposals, Tests, UpdatedAt - StartedAt, OffsetsRestored);
}

/// <summary>Persists session snapshots; every <see cref="Save"/> must be durable when it returns.</summary>
public interface ISessionStore
{
    /// <summary>Writes the snapshot and flushes it to disk.</summary>
    void Save(SessionState state);

    /// <summary>Returns the session, or null if it does not exist or is unreadable.</summary>
    SessionState? Load(string id);

    /// <summary>All readable sessions, newest first.</summary>
    IReadOnlyList<SessionState> List();
}

/// <summary>A store that keeps nothing.</summary>
public sealed class NullSessionStore : ISessionStore
{
    public static NullSessionStore Instance { get; } = new();

    public void Save(SessionState state) { }

    public SessionState? Load(string id) => null;

    public IReadOnlyList<SessionState> List() => [];
}

/// <summary>A store that keeps the latest snapshot of each session in memory.</summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, SessionState> sessions = [];
    private readonly Lock gate = new();

    public void Save(SessionState state)
    {
        lock (gate) sessions[state.Id] = state;
    }

    public SessionState? Load(string id)
    {
        lock (gate) return sessions.GetValueOrDefault(id);
    }

    public IReadOnlyList<SessionState> List()
    {
        lock (gate) return [.. sessions.Values.OrderByDescending(s => s.StartedAt)];
    }
}

/// <summary>JSON files under a directory (default %LOCALAPPDATA%\RtxAutoCurveOptimizer\sessions), one per session.</summary>
public sealed class JsonSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string directory;

    public JsonSessionStore(string? directory = null)
    {
        this.directory = directory ?? DefaultDirectory;
    }

    /// <summary>%LOCALAPPDATA%\RtxAutoCurveOptimizer\sessions.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(AppDataFolder.Path, "sessions");

    /// <summary>Writes to a temp file, flushes it to disk, then replaces the session file atomically.</summary>
    public void Save(SessionState state)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(state.Id);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, state, JsonOptions);
            stream.Flush(true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public SessionState? Load(string id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? ReadFile(path) : null;
    }

    public IReadOnlyList<SessionState> List()
    {
        if (!Directory.Exists(directory)) return [];
        return [.. Directory.EnumerateFiles(directory, "*.json")
            .Select(ReadFile)
            .OfType<SessionState>()
            .OrderByDescending(s => s.StartedAt)];
    }

    private string PathFor(string id)
    {
        if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Invalid session id.", nameof(id));
        return Path.Combine(directory, id + ".json");
    }

    private static SessionState? ReadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<SessionState>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public enum RecoveryRestore { Restored, GpuMismatch, Failed }

/// <summary>Outcome of <see cref="SessionRecovery.Recover"/>.</summary>
public sealed record RecoveryResult(SessionState Session, bool CrashedTestRecorded, RecoveryRestore Restore, string? Detail);

/// <summary>Finds a session that was not finished (crash or hang) and puts the GPU and the session back in order.</summary>
public sealed class SessionRecovery(ISessionStore store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    /// <summary>The newest session whose status is still <see cref="SessionStatus.InProgress"/>, or null.</summary>
    public SessionState? FindUnfinished() =>
        store.List().FirstOrDefault(s => s.Status == SessionStatus.InProgress);

    /// <summary>
    /// Records an in-flight test as Crashed, restores the original offsets and marks the session Interrupted.
    /// The GPU is left untouched if it does not match the session.
    /// </summary>
    public RecoveryResult Recover(SessionState session, IGpuControl gpu)
    {
        var tests = session.Tests;
        var crashed = false;
        if (session.InFlight is { } f)
        {
            tests = [.. tests, new TestRecord(
                f.Sequence, f.Kind, f.AnchorIndex, f.PointIndex, f.FrequencyKhz, f.Duration, f.Pattern, f.Intensity,
                StressOutcome.Crashed, false, TimeSpan.Zero, 0, 0, null, f.StartedAt, null)];
            crashed = true;
        }

        var restore = RecoveryRestore.Restored;
        string? detail = null;
        if (!Matches(session, gpu))
        {
            restore = RecoveryRestore.GpuMismatch;
        }
        else
        {
            try
            {
                gpu.ApplyOffsets(session.OriginalOffsets);
            }
            catch (GpuControlException ex)
            {
                restore = RecoveryRestore.Failed;
                detail = ex.Message;
            }
        }

        var updated = session with
        {
            Status = SessionStatus.Interrupted,
            Tests = tests,
            InFlight = null,
            OffsetsRestored = restore == RecoveryRestore.Restored,
            UpdatedAt = time.GetUtcNow(),
        };
        store.Save(updated);
        return new RecoveryResult(updated, crashed, restore, detail);
    }

    /// <summary>True if the GPU is the one the session was recorded on.</summary>
    public static bool Matches(SessionState session, IGpuControl gpu) =>
        session.Gpu.DeviceId == gpu.Identity.DeviceId
        && session.Gpu.SubsystemId == gpu.Identity.SubsystemId
        && session.Gpu.BusNumber == gpu.Identity.BusNumber
        && session.OriginalOffsets.OffsetsKhz.Count == session.Stock.Points.Count
        && gpu.ReadStockCurve().Points.Count == session.Stock.Points.Count;
}
