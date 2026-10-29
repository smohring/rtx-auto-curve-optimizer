using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Optimization.Tests;

public class CurveOffsetsTests
{
    [Fact]
    public void Zero_ReturnsRequestedCountOfZeros()
    {
        var offsets = CurveOffsets.Zero(5);

        Assert.Equal(5, offsets.OffsetsKhz.Count);
        Assert.All(offsets.OffsetsKhz, o => Assert.Equal(0, o));
    }
}
