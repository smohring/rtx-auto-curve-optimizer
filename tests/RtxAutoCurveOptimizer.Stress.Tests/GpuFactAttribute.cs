namespace RtxAutoCurveOptimizer.Stress.Tests;

/// <summary>Fact that is skipped when no hardware NVIDIA adapter is present.</summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (!ComputeStressTester.NvidiaAdapterAvailable())
        {
            Skip = "No NVIDIA adapter available.";
        }
    }
}
