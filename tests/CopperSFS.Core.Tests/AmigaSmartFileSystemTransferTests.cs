using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void ReadsAcrossFragmentedExtentsPreserveUnalignedRangesAndGuards(uint alignment)
    {
        foreach (var position in new uint[] { 0, 1, 511, 512, 513, 1023, 1299, 1300 })
        foreach (var length in new uint[] { 0, 1, 511, 512, 513, 1300 })
        {
            var disk = new Disk(Image()) { RequireAlignedBlockBuffers = true };
            Array.Fill(disk.Ram, (byte)0xA7);
            var destination = 4000u + alignment;
            var count = Math.Min(length, 1300 - position);
            Assert.Equal((int)count, AmigaSmartFileSystemCore.Read(ref disk, 3, position,
                APTR.FromPointer(destination), length, APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
            Assert.Equal(DOS.Error.None, error);
            for (var index = 3999u; index < destination + length + 1; index++)
                Assert.Equal(index >= destination && index < destination + count
                    ? (byte)((position + index - destination) * 17) : (byte)0xA7, disk.Ram[index]);
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void CompleteAndPartialOverwritesPreserveBytesOutsideTheCallerRange(uint alignment)
    {
        foreach (var position in new uint[] { 0, 1, 511, 512 })
        {
            var disk = new Disk(Image()) { Buffered = true, RequireAlignedBlockBuffers = true };
            var work = BatchWork(ref disk);
            var source = 4000u + alignment;
            const uint length = 788;
            for (var index = 0u; index < length; index++) disk.Ram[source + index] = (byte)(index * 29 + 3);
            var write = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Position = position, Buffer = APTR.FromPointer(source), Length = length };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref write, out var actual), work.Error.ToString());
            Assert.Equal(length, actual);
            var remounted = new Disk(disk.Snapshot()) { RequireAlignedBlockBuffers = true };
            Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref remounted, 3, 0,
                APTR.FromPointer(4000), 1300, APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
            Assert.Equal(DOS.Error.None, error);
            for (var index = 0u; index < 1300; index++)
                Assert.Equal(index >= position && index < position + length
                    ? (byte)((index - position) * 29 + 3) : (byte)(index * 17), remounted.Ram[4000 + index]);
        }
    }
}
