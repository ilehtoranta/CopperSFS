using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrstGroupPreservesRecyclingPolicyAcrossFreshWorkRecords(bool sensitive)
    {
        var image = RecycledImage();
        foreach (var block in new[] { 0, Blocks - 1 })
        {
            Block(image, block)[20] = (byte)(64 | (sensitive ? 128 : 0));
            Sum(Block(image, block));
        }
        var disk = new Disk(image) { Buffered = true, CheckReadOnlyViews = true };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        // Each GroupWrite constructs a new Work. The second Write restores the
        // group's root policy instead of reading the on-disk root again.
        Assert.True(GroupWrite(ref disk, 4, 5, Enumerable.Repeat((byte)0x43, 1000).ToArray()));
        Assert.True(GroupWrite(ref disk, 4, 1005, Enumerable.Repeat((byte)0x71, 1024).ToArray()));
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref remount, ref work));
        AssertRecycledCounters(ref remount, 1, 4);
        var bytes = GroupRead(ref remount, 4);
        Assert.Equal(2029, bytes.Length);
        Assert.All(bytes.Skip(5).Take(1000), b => Assert.Equal((byte)0x43, b));
        Assert.All(bytes.Skip(1005), b => Assert.Equal((byte)0x71, b));
    }
}
