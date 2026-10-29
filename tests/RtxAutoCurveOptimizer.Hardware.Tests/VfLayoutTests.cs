using System.Buffers.Binary;
using RtxAutoCurveOptimizer.Core;

namespace RtxAutoCurveOptimizer.Hardware.Tests;

public class VfLayoutTests
{
    private const int GraphicsCount = 5;
    private const int MemoryCount = 4;

    // Points 5,6 are type 1; 7,8 mimic the driver quirk of memory points reported as type 0.
    private static uint TypeOf(int i) => i < GraphicsCount || i >= GraphicsCount + 2 ? 0u : 1u;

    // Points 0..4 graphics (ascending voltage), 5..8 memory.
    private static uint[] Mask()
    {
        var mask = new uint[8];
        mask[0] = (1u << (GraphicsCount + MemoryCount)) - 1;
        return mask;
    }

    private static int VoltageUv(int i) => 500_000 + i * 100_000;
    private static int StockKhz(int i) => 300_000 + i * 500_000;
    private static int OffsetKhz(int i) => i * 1_000;

    private static byte[] SyntheticStatus()
    {
        var b = VfLayout.CreateBuffer(VfLayout.StatusSize, Mask());
        for (var i = 0; i < GraphicsCount + MemoryCount; i++)
        {
            var o = VfLayout.EntryOffset(i, VfLayout.StatusEntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), TypeOf(i));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), (uint)(StockKhz(i) + OffsetKhz(i)));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 8), (uint)VoltageUv(i));
        }
        return b;
    }

    // Control entries carry marker bytes everywhere so preservation can be checked.
    private static byte[] SyntheticControl()
    {
        var b = VfLayout.CreateBuffer(VfLayout.ControlSize, Mask());
        for (var i = 0; i < GraphicsCount + MemoryCount; i++)
        {
            var o = VfLayout.EntryOffset(i, VfLayout.ControlEntrySize);
            for (var k = 0; k < VfLayout.ControlEntrySize; k++) b[o + k] = (byte)(0x40 + i + k);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), TypeOf(i));
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + VfLayout.ControlOffsetField), OffsetKhz(i));
        }
        b[VfLayout.HeaderSize - 1] = 0xAB; // reserved header byte
        b[^1] = 0xCD;                      // trailing unknown byte
        return b;
    }

    [Theory]
    [InlineData(VfLayout.InfoSize, 0x182C)]
    [InlineData(VfLayout.StatusSize, 0x1C28)]
    [InlineData(VfLayout.ControlSize, 0x2420)]
    [InlineData(VfLayout.VoltRailsSize, 0x4C)]
    public void Sizes_MatchSpec(int actual, int expected) => Assert.Equal(expected, actual);

    [Fact]
    public void VersionField_CombinesSizeAndVersion()
    {
        Assert.Equal(0x00011C28u, VfLayout.VersionField(VfLayout.StatusSize));
        Assert.Equal(0x0001004Cu, VfLayout.VersionField(VfLayout.VoltRailsSize));
    }

    [Fact]
    public void CreateBuffer_SetsVersionAndMask()
    {
        var mask = new uint[] { 1, 2, 3, 4, 5, 6, 7, 0x80000000 };
        var b = VfLayout.CreateBuffer(VfLayout.ControlSize, mask);

        Assert.Equal(VfLayout.ControlSize, b.Length);
        Assert.Equal(0x00012420u, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal(mask, VfLayout.ReadMask(b));
        Assert.Equal(0x80000000u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4 + 7 * 4)));
        Assert.All(b.AsSpan(VfLayout.HeaderSize).ToArray(), x => Assert.Equal(0, x));
    }

    [Fact]
    public void CreateBuffer_WithoutMask_OnlyVersionIsSet()
    {
        var b = VfLayout.CreateBuffer(VfLayout.VoltRailsSize);

        Assert.Equal(0x0001004Cu, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.All(b.AsSpan(4).ToArray(), x => Assert.Equal(0, x));
    }

    [Fact]
    public void CreateBuffer_RejectsWrongMaskLength() =>
        Assert.Throws<ArgumentException>(() => VfLayout.CreateBuffer(VfLayout.InfoSize, new uint[3]));

    [Fact]
    public void IsPointSet_ChecksBits()
    {
        var mask = new uint[8];
        mask[1] = 1u << 3;

        Assert.True(VfLayout.IsPointSet(mask, 35));
        Assert.False(VfLayout.IsPointSet(mask, 34));
        Assert.False(VfLayout.IsPointSet(mask, 256));
    }

    [Fact]
    public void EntryOffset_StartsAfterHeader()
    {
        Assert.Equal(68, VfLayout.EntryOffset(0, VfLayout.ControlEntrySize));
        Assert.Equal(68 + 3 * 28, VfLayout.EntryOffset(3, VfLayout.StatusEntrySize));
    }

    [Fact]
    public void DecodeStatus_ReadsTypeFrequencyVoltage()
    {
        var points = VfLayout.DecodeStatus(SyntheticStatus(), Mask());

        Assert.Equal(GraphicsCount + MemoryCount, points.Count);
        Assert.Equal(new StatusPoint(2, 0, StockKhz(2) + OffsetKhz(2), VoltageUv(2)), points[2]);
        Assert.Equal(1u, points[5].Type);
    }

    [Fact]
    public void DecodeStatus_SkipsPointsOutsideMask()
    {
        var mask = new uint[8];
        mask[0] = 0b101;

        var points = VfLayout.DecodeStatus(SyntheticStatus(), mask);

        Assert.Equal([0, 2], points.Select(p => p.Index));
    }

    [Fact]
    public void DecodeControl_ReadsOffsetAtField20()
    {
        var points = VfLayout.DecodeControl(SyntheticControl(), Mask());

        Assert.Equal(new ControlPoint(3, 0, OffsetKhz(3)), points[3]);
        Assert.Equal(1u, points[6].Type);
    }

    [Fact]
    public void DecodeControl_NegativeOffset_IsSigned()
    {
        var control = SyntheticControl();
        var o = VfLayout.EntryOffset(1, VfLayout.ControlEntrySize) + VfLayout.ControlOffsetField;
        BinaryPrimitives.WriteInt32LittleEndian(control.AsSpan(o), -150_000);

        Assert.Equal(-150_000, VfLayout.DecodeControl(control, Mask())[1].OffsetKhz);
    }

    [Fact]
    public void DecodeStatus_EntryBeyondBuffer_Throws()
    {
        var mask = new uint[8];
        mask[7] = 0x80000000; // point 255 does not fit in a short buffer

        Assert.Throws<ArgumentException>(() => VfLayout.DecodeStatus(new byte[200], mask));
    }

    [Fact]
    public void DecodeGraphicsPoints_ReturnsOnlyGraphicsWithStockEqualStatusMinusOffset()
    {
        var points = VfLayout.DecodeGraphicsPoints(SyntheticStatus(), SyntheticControl(), Mask());

        Assert.Equal(GraphicsCount, points.Count);
        for (var i = 0; i < GraphicsCount; i++)
        {
            Assert.Equal(i, points[i].Index);
            Assert.Equal(VoltageUv(i), points[i].VoltageUv);
            Assert.Equal(OffsetKhz(i), points[i].OffsetKhz);
            Assert.Equal(StockKhz(i), points[i].StockKhz);
        }
    }

    [Fact]
    public void DecodeGraphicsPoints_IgnoresType0EntriesAfterFirstMemoryEntry()
    {
        var points = VfLayout.DecodeGraphicsPoints(SyntheticStatus(), SyntheticControl(), Mask());

        Assert.DoesNotContain(points, p => p.Index >= GraphicsCount);
    }

    [Fact]
    public void DecodeGraphicsPoints_SortsByVoltageKeepingDriverIndex()
    {
        var status = SyntheticStatus();
        // Swap the voltages of points 0 and 4 so index order differs from voltage order.
        BinaryPrimitives.WriteUInt32LittleEndian(
            status.AsSpan(VfLayout.EntryOffset(0, VfLayout.StatusEntrySize) + 8), (uint)VoltageUv(4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            status.AsSpan(VfLayout.EntryOffset(4, VfLayout.StatusEntrySize) + 8), (uint)VoltageUv(0));

        var points = VfLayout.DecodeGraphicsPoints(status, SyntheticControl(), Mask());

        Assert.Equal([4, 1, 2, 3, 0], points.Select(p => p.Index));
        Assert.True(points.Zip(points.Skip(1)).All(p => p.First.VoltageUv <= p.Second.VoltageUv));
    }

    [Fact]
    public void ToStockCurve_UsesDriverIndexAndStock()
    {
        var curve = VfLayout.ToStockCurve(VfLayout.DecodeGraphicsPoints(SyntheticStatus(), SyntheticControl(), Mask()));

        Assert.Equal(new CurvePoint(3, VoltageUv(3), StockKhz(3)), curve.Points[3]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(300_000, 300_000)]
    [InlineData(1_000_000, 1_000_000)]
    [InlineData(1_000_001, 1_000_000)]
    [InlineData(5_000_000, 1_000_000)]
    [InlineData(-1_000_001, -1_000_000)]
    [InlineData(int.MinValue, -1_000_000)]
    public void ClampOffsetKhz_LimitsToPlusMinus1000Mhz(int input, int expected) =>
        Assert.Equal(expected, VfLayout.ClampOffsetKhz(input));

    [Fact]
    public void PatchGraphicsOffsets_ChangesOnlyOffsetBytesOfGraphicsEntries()
    {
        var control = SyntheticControl();
        var indices = Enumerable.Range(0, GraphicsCount).ToArray();
        var offsets = new[] { 100_000, -50_000, 0, 750_000, 2_000_000 };

        var patched = VfLayout.PatchGraphicsOffsets(control, indices, offsets);

        Assert.Equal(control.Length, patched.Length);
        var expected = (byte[])control.Clone();
        var clamped = new[] { 100_000, -50_000, 0, 750_000, 1_000_000 };
        for (var n = 0; n < GraphicsCount; n++)
            BinaryPrimitives.WriteInt32LittleEndian(
                expected.AsSpan(VfLayout.EntryOffset(n, VfLayout.ControlEntrySize) + VfLayout.ControlOffsetField),
                clamped[n]);
        Assert.Equal(expected, patched);
    }

    [Fact]
    public void PatchGraphicsOffsets_PreservesMemoryEntriesHeaderAndTrailingBytes()
    {
        var control = SyntheticControl();

        var patched = VfLayout.PatchGraphicsOffsets(control, [0, 1, 2, 3, 4], [1, 2, 3, 4, 5]);

        var memStart = VfLayout.EntryOffset(GraphicsCount, VfLayout.ControlEntrySize);
        Assert.Equal(control.AsSpan(memStart).ToArray(), patched.AsSpan(memStart).ToArray());
        Assert.Equal(control.AsSpan(0, VfLayout.HeaderSize).ToArray(), patched.AsSpan(0, VfLayout.HeaderSize).ToArray());
        Assert.Equal(0xCD, patched[^1]);
    }

    [Fact]
    public void PatchGraphicsOffsets_DoesNotModifyInput()
    {
        var control = SyntheticControl();
        var copy = (byte[])control.Clone();

        VfLayout.PatchGraphicsOffsets(control, [0], [123_000]);

        Assert.Equal(copy, control);
    }

    [Fact]
    public void PatchGraphicsOffsets_RespectsGivenPointOrder()
    {
        var patched = VfLayout.PatchGraphicsOffsets(SyntheticControl(), [3, 1], [30_000, 10_000]);

        var decoded = VfLayout.DecodeControl(patched, Mask());
        Assert.Equal(30_000, decoded[3].OffsetKhz);
        Assert.Equal(10_000, decoded[1].OffsetKhz);
        Assert.Equal(OffsetKhz(0), decoded[0].OffsetKhz);
    }

    [Fact]
    public void PatchGraphicsOffsets_RejectsCountMismatch() =>
        Assert.Throws<ArgumentException>(() => VfLayout.PatchGraphicsOffsets(SyntheticControl(), [0, 1], [1]));

    [Fact]
    public void PatchGraphicsOffsets_RejectsMemoryEntry() =>
        Assert.Throws<ArgumentException>(() =>
            VfLayout.PatchGraphicsOffsets(SyntheticControl(), [GraphicsCount], [1]));

    [Fact]
    public void DecodeVoltageUv_ReadsByte40()
    {
        var b = VfLayout.CreateBuffer(VfLayout.VoltRailsSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), 812_500);

        Assert.Equal(812_500, VfLayout.DecodeVoltageUv(b));
    }

    [Fact]
    public void FormatDriverVersion_InsertsDecimalPoint()
    {
        Assert.Equal("621.01", VfLayout.FormatDriverVersion(62101));
        Assert.Equal("572.16", VfLayout.FormatDriverVersion(57216));
    }
}
