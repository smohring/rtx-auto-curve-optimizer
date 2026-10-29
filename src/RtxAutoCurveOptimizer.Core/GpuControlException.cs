namespace RtxAutoCurveOptimizer.Core;

/// <summary>A GPU control call failed; carries the NVAPI status when known.</summary>
public class GpuControlException(string message, int? nvapiStatus = null) : Exception(message)
{
    public int? NvapiStatus { get; } = nvapiStatus;
}

/// <summary>The driver refused a write because the process is not elevated (NVAPI -137).</summary>
public sealed class GpuAccessDeniedException(string message) : GpuControlException(message, -137);
