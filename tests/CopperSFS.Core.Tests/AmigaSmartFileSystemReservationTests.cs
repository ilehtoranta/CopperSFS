using System.Buffers.Binary;
using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ReservedContinuationKeepsOneExtentAndRecoversEveryCut(bool buffered, bool combined)
    {
        var seed = LinearRangeFixture();
        var first = new Disk(seed.Image.ToArray()) { Buffered = true };
        var work = RangeWork(ref first, buffered, combined);
        Assert.True(WriteLinearRange(ref first, ref work, seed.File, 0, out _));
        Assert.True(AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref first, ref work, seed.File));
        var image = first.Snapshot();
        var reservations = first.Ram.AsSpan(420000, 260).ToArray();
        var probe = new Disk(image.ToArray()) { Buffered = true };
        work = RangeWork(ref probe, buffered, combined); reservations.CopyTo(probe.Ram, 420000);
        Assert.True(ReservedTail(ref probe, ref work, seed.File));
        Assert.False(AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref probe, ref work, seed.File));
        Assert.All(ReadLinearRange(ref probe, seed.File, out var blocks), b => Assert.Equal(0xA7, b));
        Assert.Equal(Enumerable.Range((int)blocks[0], 192).Select(n => (uint)n), blocks);
        var free = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(2 * Bytes + Bytes - 28));
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var disk = new Disk(image.ToArray()) { Buffered = true };
            work = RangeWork(ref disk, buffered, combined); reservations.CopyTo(disk.Ram, 420000);
            disk.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() => ReservedTail(ref disk, ref work, seed.File));
            var recovered = new Disk(disk.Snapshot()); var recovery = RangeWork(ref recovered, buffered, combined);
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref recovery), $"Cut {cut}: {recovery.Error}");
            var bytes = ReadLinearRange(ref recovered, seed.File, out _);
            Assert.All(bytes[..65536], b => Assert.Equal(0xA7, b));
            Assert.True(bytes[65536..].All(b => b == 0x31) || bytes[65536..].All(b => b == 0xA7), $"Mixed tail at cut {cut}");
            Assert.All(RangeRead(ref recovered, seed.Neighbor), b => Assert.Equal(0x71, b));
            Assert.Equal(free, BinaryPrimitives.ReadUInt32BigEndian(recovered.Snapshot().AsSpan(2 * Bytes + Bytes - 28)));
        }
    }

    private static bool ReservedTail(ref Disk disk, ref AmigaSmartFileSystemWriteCore.Work work, uint node)
    {
        Array.Fill(disk.Ram, (byte)0xA7, (int)RangePayload.Raw, 32768);
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = node, Position = 65536, Buffer = RangePayload, Length = 32768 };
        return AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request, RangeScratch, 65536, out _);
    }
}
