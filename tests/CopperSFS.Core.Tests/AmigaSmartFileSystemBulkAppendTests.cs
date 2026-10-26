using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockedAppendFindsAnotherBulkRunAndRecoversEveryCut(bool buffered)
    {
        var seed = new Disk(Image());
        var work = BatchWork(ref seed);
        uint Create(string name)
        {
            var request = new AmigaSmartFileSystemWriteCore.Request
                { Directory = 1, Name = seed.Name(name), Length = (uint)name.Length };
            Assert.True(AmigaSmartFileSystemWriteCore.Create(ref seed, ref work, ref request, false));
            return request.Node;
        }
        var node = Create("Bulk"); var neighbor = Create("Adjacent");
        work.AllocationCursor = 40; work.JournalCursor = 100;
        Array.Fill(seed.Ram, (byte)0x31, 4000, 1024);
        var initial = new AmigaSmartFileSystemWriteCore.Request
            { Node = node, Buffer = APTR.FromPointer(4000), Length = 1024 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref seed, ref work, ref initial, out _));
        work.AllocationCursor = 42;
        initial.Node = neighbor; initial.Length = 512;
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref seed, ref work, ref initial, out _));
        var image = seed.Snapshot();
        var before = AllocatedState(image);

        Disk Start() => new(image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        bool Append(ref Disk disk)
        {
            var state = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
            state.AllocationCursor = 42; state.JournalCursor = 100;
            Array.Fill(disk.Ram, (byte)0xA7, 4000, 4096);
            var request = new AmigaSmartFileSystemWriteCore.Request
                { Node = node, Position = 1024, Buffer = APTR.FromPointer(4000), Length = 4096 };
            return AmigaSmartFileSystemWriteCore.Write(ref disk, ref state, ref request, out var actual) &&
                actual == 4096 && AmigaSmartFileSystemWriteCore.Flush(ref disk, ref state);
        }
        var probe = Start();
        Assert.True(Append(ref probe));
        Assert.Contains(probe.DataWrites, run => run.Count == 8);
        var after = AllocatedState(probe.Snapshot());
        Assert.Equal(512, AmigaSmartFileSystemCore.Read(ref probe, neighbor, 0, APTR.FromPointer(32768), 512,
            APTR.FromPointer(512), APTR.FromPointer(1024), out _));
        Assert.All(probe.Ram.AsSpan(32768, 512).ToArray(), b => Assert.Equal(0x31, b));
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var disk = Start(); disk.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() => Append(ref disk));
            var recovered = new Disk(disk.Snapshot());
            var state = BatchWork(ref recovered);
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref state));
            var actual = AllocatedState(recovered.Snapshot());
            Assert.True(actual.SequenceEqual(before) || actual.SequenceEqual(after), $"Mixed append at cut {cut}");
        }
    }
}
