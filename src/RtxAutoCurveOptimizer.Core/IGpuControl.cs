namespace RtxAutoCurveOptimizer.Core;

/// <summary>Reads and writes the V/F curve and telemetry of one GPU.</summary>
public interface IGpuControl : IDisposable
{
    GpuIdentity Identity { get; }

    /// <summary>True if the process is elevated and may write the curve.</summary>
    bool CanWrite { get; }

    StockCurve ReadStockCurve();

    CurveOffsets ReadOffsets();

    /// <exception cref="GpuAccessDeniedException">The process is not elevated.</exception>
    /// <exception cref="GpuControlException">The driver rejected the call.</exception>
    void ApplyOffsets(CurveOffsets offsets);

    /// <exception cref="GpuControlException">Telemetry is unavailable, e.g. after a driver reset.</exception>
    Telemetry ReadTelemetry();

    /// <summary>Drops and reopens all driver handles; needed after a driver reset (TDR) invalidated them.</summary>
    /// <exception cref="GpuControlException">The GPU could not be reopened.</exception>
    void Reinitialize();
}
