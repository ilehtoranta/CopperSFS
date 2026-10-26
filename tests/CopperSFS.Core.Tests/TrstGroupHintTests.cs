using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Fact]
    public void TrstGroupRecordHintIsScopedToConsecutiveAcceptedWrites()
    {
        var seed = GroupSeed(2048);
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        var work = TrstWork();
        var create = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = disk.Name("Other"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref create, false));
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var first = Enumerable.Repeat((byte)0xA1, 512).ToArray();
        var second = Enumerable.Repeat((byte)0xB2, 512).ToArray();
        Assert.True(GroupWrite(ref disk, seed.Node, 0, first));
        var nodes = disk.ReadUInt32(GroupAddress, 36);
        disk.FailReadBlock = nodes;
        // The location is already validated; a consecutive Write needs no
        // object-node tree read. The metadata view itself is still checked.
        Assert.True(GroupWrite(ref disk, seed.Node, 512, second));
        disk.FailReadBlock = uint.MaxValue;
        Assert.True(GroupWrite(ref disk, create.Node, 0, first));
        var expected = seed.Bytes.ToArray(); first.CopyTo(expected, 0); second.CopyTo(expected, 512);
        Assert.Equal(expected, GroupRead(ref disk, seed.Node));
        Assert.Equal(first, GroupRead(ref disk, create.Node));

        disk.FailAtWrite = disk.Writes + 1;
        Assert.False(GroupWrite(ref disk, seed.Node, 1024, second));
        disk.FailAtWrite = 0;
        Assert.Equal(expected, GroupRead(ref disk, seed.Node));
        // Failure discards even an otherwise usable hint. A different file
        // and a retry must resolve their own location before updating it.
        disk.FailReadBlock = nodes;
        Assert.False(GroupWrite(ref disk, create.Node, 512, second));
        disk.FailReadBlock = uint.MaxValue;
        Assert.True(GroupWrite(ref disk, create.Node, 512, second));
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        disk.FailReadBlock = nodes;
        Assert.False(GroupWrite(ref disk, create.Node, 0, second));
        disk.FailReadBlock = uint.MaxValue;
        Assert.True(GroupWrite(ref disk, seed.Node, 1024, second));
        second.CopyTo(expected, 1024);
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(expected, GroupRead(ref remount, seed.Node));
        Assert.Equal(first.Concat(second).ToArray(), GroupRead(ref remount, create.Node));
    }

    [Theory]
    [InlineData(4, uint.MaxValue)]
    [InlineData(8, 0u)]
    [InlineData(8, uint.MaxValue)]
    public void TrstGroupRejectsInvalidPrivateRecordHintsAndRetainsAcceptedData(int field, uint value)
    {
        var seed = GroupSeed(2048);
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var first = Enumerable.Repeat((byte)0xD3, 512).ToArray();
        var second = Enumerable.Repeat((byte)0xE4, 512).ToArray();
        Assert.True(GroupWrite(ref disk, seed.Node, 0, first));
        var active = disk.RedoOverlay;
        // The final twelve header bytes are the owned node/container/offset
        // tuple. Corruption must not make a partial Write visible.
        disk.WriteUInt32(GroupAddress, 64 + field, value);
        var writes = disk.PhysicalWrites.Count;
        Assert.False(GroupWrite(ref disk, seed.Node, 512, second));
        Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(writes, disk.PhysicalWrites.Count);
        var expected = seed.Bytes.ToArray(); first.CopyTo(expected, 0);
        Assert.Equal(expected, GroupRead(ref disk, seed.Node));
        Assert.True(GroupWrite(ref disk, seed.Node, 512, second));
        second.CopyTo(expected, 512);
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(expected, GroupRead(ref remount, seed.Node));
    }
}
