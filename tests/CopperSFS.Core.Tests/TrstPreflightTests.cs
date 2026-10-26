using System.Reflection;
using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private struct RangeMemory((uint Start, uint End)[] ranges) : IAmigaGuestMemory
    {
        public int Checks;
        public bool IsMapped(APTR address, uint size)
        {
            Checks++;
            if (address.Raw > uint.MaxValue - size) return false;
            foreach (var range in ranges)
                if (address.Raw >= range.Start && address.Raw + size <= range.End) return true;
            return false;
        }
        public byte ReadUInt8(APTR a, int o = 0) => throw new InvalidOperationException();
        public ushort ReadUInt16(APTR a, int o = 0) => throw new InvalidOperationException();
        public uint ReadUInt32(APTR a, int o = 0) => throw new InvalidOperationException();
        public void WriteUInt8(APTR a, int o, byte v) => throw new InvalidOperationException();
        public void WriteUInt16(APTR a, int o, ushort v) => throw new InvalidOperationException();
        public void WriteUInt32(APTR a, int o, uint v) => throw new InvalidOperationException();
        public void Clear(APTR a, uint n) => throw new InvalidOperationException();
        public void Copy(APTR a, APTR b, uint n) => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)]
    public void TrstWorkspaceEnvelopePreservesExactRangesAndFallbacks(int scenario)
    {
        uint[] buffers = [0x1000, 0x1200, 0x1400, 0x1600];
        (uint Start, uint End)[] ranges = [(0x1000, 0x7000)];
        uint group = 0x6000, span = 0x1000;
        if (scenario == 1) buffers = [0x1600, 0x1200, 0x1400, 0x1000];
        if (scenario == 2) ranges = [(0x1000, 0x1200), (0x1200, 0x1400), (0x1400, 0x1600), (0x1600, 0x1800)];
        if (scenario == 3) { buffers = [0x1000, 0x1200, 0x5000, 0x5200]; group = 0x2000; span = 0x2000; }
        if (scenario == 4) buffers[2] = group;
        if (scenario == 5) group = 0x1700;
        if (scenario == 6) group = 0x1800;
        if (scenario == 7) buffers[3] = 0xFFFFFEF0;
        if (scenario == 8) buffers[1] = 0;
        if (scenario == 9) buffers = [0x1000, 0x1000, 0x1000, 0x1000];
        if (scenario == 10) { buffers = [0xFFFFFDFF, 0xFFFFFDFF, 0xFFFFFDFF, 0xFFFFFDFF]; ranges = [(0xFFFFFDFF, uint.MaxValue)]; }
        if (scenario == 11) ranges = [(0x1000, 0x1400), (0x1600, 0x1800)];
        var memory = new RangeMemory(ranges);
        var work = new AmigaSmartFileSystemWriteCore.Work
        {
            Meta = APTR.FromPointer(buffers[0]), Data = APTR.FromPointer(buffers[1]),
            Bitmap = APTR.FromPointer(buffers[2]), Journal = APTR.FromPointer(buffers[3])
        };
        // An independent half-open interval oracle also covers boundaries near
        // 2^32; widened arithmetic cannot accidentally bless address wrap.
        var expected = buffers.All(b => b != 0 && (ulong)b + 512 <= uint.MaxValue &&
            ranges.Any(r => b >= r.Start && (ulong)b + 512 <= r.End) &&
            !((ulong)b < (ulong)group + span && (ulong)group < (ulong)b + 512));
        var method = typeof(AmigaSmartFileSystemWriteCore).GetMethod("TrstWorkspaceFits",
            BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(RangeMemory));
        object[] args = [memory, work, APTR.FromPointer(group), span, 512u];
        Assert.Equal(expected, (bool)method.Invoke(null, args)!);
        memory = (RangeMemory)args[0];
        if (scenario is 0 or 1 or 6 or 9 or 10) Assert.Equal(1, memory.Checks);
        if (scenario is 2 or 3) Assert.Equal(5, memory.Checks);
        if (scenario == 8) Assert.Equal(0, memory.Checks);
    }

    [Theory]
    [InlineData(32)] [InlineData(36)] [InlineData(40)]
    public void TrstCachedRecordOffsetsRejectChangedHintsBeforeAnyWrite(int field)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected);
        var active = disk.RedoOverlay; var writes = disk.Writes;
        disk.WriteUInt32(GroupAddress, 76 + field, disk.ReadUInt32(GroupAddress, 76 + field) + 2);
        Assert.False(GroupWrite(ref disk, 3, 3072, new byte[1024]));
        Assert.Equal(writes, disk.Writes); Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(expected, GroupRead(ref disk, 3));
    }

    [Fact]
    public void WriteWorkspaceOverlapMatchesWidenedIntervalsIncludingAddressWrap()
    {
        var method = typeof(AmigaSmartFileSystemWriteCore).GetMethod("Overlap",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        uint[] values = [0, 1, 511, 512, 513, 0x7FFFFFFF, 0x80000000, 0xFFFFFE00, uint.MaxValue];
        foreach (var a in values) foreach (var b in values)
        foreach (var lengthA in values) foreach (var lengthB in values)
        {
            var expected = lengthA != 0 && lengthB != 0 &&
                (ulong)a < (ulong)b + lengthB && (ulong)b < (ulong)a + lengthA;
            object[] args = [APTR.FromPointer(a), lengthA, APTR.FromPointer(b), lengthB];
            Assert.Equal(expected, (bool)method.Invoke(null, args)!);
        }
    }
}
