using System.Text.Json.Serialization;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization;

/// <summary>What a single test measures.</summary>
public enum TestKind { Warmup, ReferenceStock, ReferenceCurrent, Search, Confirm, Validation, ColdConfirm }

/// <summary>Which stock/current curve a reference measurement describes.</summary>
public enum ReferenceKind { Stock, Current }

/// <summary>Averages and medians of the telemetry samples taken during one test (loaded samples only when available).</summary>
public sealed record TelemetryAverages(
    int SampleCount,
    double AvgClockMhz, double AvgVoltageUv, double AvgPowerW, double AvgTemperatureC,
    double MedianClockMhz, double MedianVoltageUv,
    ClockLimitReasons LimitReasons, double MaxClockMhz = 0);

/// <summary>One finished test. AnchorIndex and PointIndex are -1 for tests that are not tied to an anchor.</summary>
public sealed record TestRecord(
    int Sequence, TestKind Kind, int AnchorIndex, int PointIndex, int FrequencyKhz,
    TimeSpan Duration, LoadPattern Pattern, double Intensity,
    StressOutcome Outcome, bool Valid, TimeSpan Elapsed, long MismatchCount, double Throughput,
    TelemetryAverages? Telemetry, DateTimeOffset StartedAt, string? Detail, bool TelemetryLost = false)
{
    /// <summary>True if the stress test passed and the telemetry confirmed the intended operating point.</summary>
    [JsonIgnore]
    public bool Passed => Outcome == StressOutcome.Passed && Valid;
}

/// <summary>The test that was about to start (or running) when a session was last written.</summary>
public sealed record InFlightTest(
    int Sequence, TestKind Kind, int AnchorIndex, int PointIndex, int FrequencyKhz,
    TimeSpan Duration, LoadPattern Pattern, double Intensity, DateTimeOffset StartedAt);

/// <summary>A Steady measurement at the stock or the original curve.</summary>
public sealed record ReferenceMeasurement(ReferenceKind Kind, TelemetryAverages Averages, double Throughput);

/// <summary>
/// Result of one anchor search. PointIndex is the position in <see cref="StockCurve.Points"/>;
/// averages come from the Steady part of the confirm test. ColdProvenKhz is the highest clock a cold burst
/// actually ran without errors (0 if none).
/// </summary>
public sealed record AnchorResult(
    int AnchorIndex, int PointIndex, int VoltageUv, int StockKhz,
    int MaxStableKhz, int RecommendedKhz,
    double AvgPowerW, double AvgClockMhz, double AvgVoltageUv, double AvgTemperatureC,
    double Throughput, double ThroughputRelative, double PerfPerWatt,
    bool Limited, double Intensity, int ColdProvenKhz = 0);

public enum ProposalKind { Efficient, MaxEfficiency, Performance, Custom, Uniform }

/// <summary>A complete offset set capped at one anchor; <see cref="ProposalKind.Efficient"/> is the recommended one.</summary>
public sealed record ProfileProposal(
    ProposalKind Kind, AnchorResult CapAnchor, CurveOffsets Offsets, int ExtraMarginMhz,
    double ExpectedPowerW, double ExpectedRelativePerformance, bool Validated);

public enum OptimizationStatus { Completed, Cancelled, Failed }

public enum OptimizationFailure
{
    None,
    GpuAccessDenied,
    GpuControlError,
    StockUnstable,
    NoUsableAnchors,
    SessionMismatch,
    /// <summary>After a driver reset the GPU could not be reopened or ignored curve writes; the run stopped.</summary>
    GpuUnresponsive,
}

/// <summary>Everything a finished, cancelled or failed run produced.</summary>
public sealed record OptimizationResult(
    OptimizationStatus Status, OptimizationFailure Failure, string? FailureDetail,
    string SessionId, OptimizerSettings Settings, StockCurve Stock, CurveOffsets OriginalOffsets,
    ReferenceMeasurement? StockReference, ReferenceMeasurement? CurrentReference,
    IReadOnlyList<AnchorResult> Anchors, IReadOnlyList<ProfileProposal> Proposals,
    IReadOnlyList<TestRecord> Tests, TimeSpan Elapsed, bool OffsetsRestored)
{
    /// <summary>The proposal marked as recommended, if one was built.</summary>
    public ProfileProposal? Recommended => Proposals.FirstOrDefault(p => p.Kind == ProposalKind.Efficient);
}
