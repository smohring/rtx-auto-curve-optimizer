using System.Text.Json.Serialization;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization;

public enum OptimizerPhase
{
    Preparing,
    Calibrating,
    WarmingUp,
    MeasuringReferences,
    SearchingAnchors,
    BuildingProposals,
    Validating,
    Restoring,
    Finished,
}

public enum WarningKind
{
    /// <summary>The stress tester lost the device; the optimizer waits, recalibrates and continues.</summary>
    DeviceLost,
    /// <summary>A test could not reach its intended clock/voltage; intensity is lowered.</summary>
    IntensityReduced,
    /// <summary>No valid test was possible at some frequencies of the anchor; it is marked Limited.</summary>
    AnchorLimited,
    /// <summary>The anchor produced no usable result and was skipped (Detail = anchor index).</summary>
    AnchorSkipped,
    /// <summary>Confirmation kept failing and the anchor was skipped (Detail = anchor index).</summary>
    AnchorUnconfirmed,
    /// <summary>The original curve failed the Steady reference test.</summary>
    CurrentCurveUnstable,
    /// <summary>A validation failed and the safety margin was raised (Detail = new extra margin in MHz).</summary>
    MarginIncreased,
    /// <summary>The proposal could not be validated within the allowed attempts.</summary>
    ValidationFailed,
    /// <summary>Restoring the original offsets failed (Detail = error message).</summary>
    RestoreFailed,
    /// <summary>The run continues a recovered session.</summary>
    ResumedFromSession,
    /// <summary>A test got no telemetry; the GPU was reconnected and the test is repeated (Detail = test sequence).</summary>
    TelemetryLost,
    /// <summary>The test process died or hung and was restarted; the run resumes (Detail = restart number).</summary>
    WorkerRestarted,
    /// <summary>The GPU cools down before a cold confirm burst (Detail = anchor index).</summary>
    CoolingDown,
}

/// <summary>Base of all progress events; contains data only, no localized text. Serializable for the worker process.</summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(PhaseChanged), "phase")]
[JsonDerivedType(typeof(TestStarted), "testStarted")]
[JsonDerivedType(typeof(TestFinished), "testFinished")]
[JsonDerivedType(typeof(AnchorCompleted), "anchor")]
[JsonDerivedType(typeof(TelemetrySampled), "telemetry")]
[JsonDerivedType(typeof(StressProgressed), "stress")]
[JsonDerivedType(typeof(EtaUpdated), "eta")]
[JsonDerivedType(typeof(Warning), "warning")]
[JsonDerivedType(typeof(RunFinished), "finished")]
public abstract record OptimizerEvent;

/// <summary>Last event of a worker process: the run ended and its session holds the result.</summary>
public sealed record RunFinished(string SessionId) : OptimizerEvent;

public sealed record PhaseChanged(OptimizerPhase Phase) : OptimizerEvent;

public sealed record TestStarted(int AnchorIndex, int FrequencyKhz, TimeSpan Duration, TestKind Kind) : OptimizerEvent;

public sealed record TestFinished(TestRecord Record) : OptimizerEvent;

public sealed record AnchorCompleted(AnchorResult Result) : OptimizerEvent;

public sealed record TelemetrySampled(Telemetry Telemetry) : OptimizerEvent;

public sealed record StressProgressed(StressProgress Progress) : OptimizerEvent;

public sealed record EtaUpdated(TimeSpan Remaining) : OptimizerEvent;

public sealed record Warning(WarningKind Kind, string? Detail) : OptimizerEvent;
