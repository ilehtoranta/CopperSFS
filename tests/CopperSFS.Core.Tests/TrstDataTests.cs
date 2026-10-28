using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static AmigaSmartFileSystemWriteCore.Work TrstWork() => new()
    {
        Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024),
        Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048), AllocationCursor = 25
    };

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1024u, 1024u)]
    [InlineData(8192u, 0u)]
    [InlineData(8192u, 1024u)]
    [InlineData(8192u, 6144u)]
    [InlineData(8192u, 2048u, true)]
    [InlineData(8192u, 4096u, true)]
    public void TrstDataWritesPreserveWholeWriteAcrossCutsAndFailures(uint initialLength, uint position, bool priorRelocation = false)
    {
        var seed = new Disk(Image()); var w = TrstWork();
        var create = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = seed.Name("NewRun"), Length = 6 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref seed, ref w, ref create, false));
        var oldBytes = Enumerable.Range(0, (int)initialLength).Select(i => (byte)(i * 13 + 3)).ToArray();
        if (initialLength != 0)
        {
            oldBytes.CopyTo(seed.Ram, 4000);
            var initial = new AmigaSmartFileSystemWriteCore.Request { Node = create.Node, Buffer = APTR.FromPointer(4000), Length = initialLength };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref seed, ref w, ref initial, out _));
        }
        if (priorRelocation)
        {
            var prefix = Enumerable.Range(0, (int)position).Select(i => (byte)(i * 23 + 7)).ToArray();
            prefix.CopyTo(seed.Ram, 4000); prefix.CopyTo(oldBytes, 0);
            var initial = new AmigaSmartFileSystemWriteCore.Request
            { Node = create.Node, Buffer = APTR.FromPointer(4000), Length = position };
            Assert.True(AmigaSmartFileSystemWriteCore.ExecuteTrst(ref seed, ref w, ref initial,
                AmigaSmartFileSystemWriteCore.TrstOperation.Write, APTR.FromPointer(200000), 196608, out _));
        }
        var saved = seed.Snapshot();
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref seed, w.Meta, out var oldUsed));
        var payload = Enumerable.Range(0, 2048).Select(i => (byte)(i * 17 + 5)).ToArray();
        var expected = new byte[Math.Max(initialLength, position + payload.Length)];
        oldBytes.CopyTo(expected, 0); payload.CopyTo(expected, (int)position);
        bool Write(ref Disk disk, out uint actual)
        {
            payload.CopyTo(disk.Ram, 4000); var work = TrstWork();
            var r = new AmigaSmartFileSystemWriteCore.Request
            { Node = create.Node, Position = position, Buffer = APTR.FromPointer(4000), Length = (uint)payload.Length };
            return AmigaSmartFileSystemWriteCore.ExecuteTrst(ref disk, ref work, ref r,
                AmigaSmartFileSystemWriteCore.TrstOperation.Write, APTR.FromPointer(200000), 196608, out actual);
        }
        void Check(byte[] image)
        {
            var disk = new Disk(image); var work = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
            Assert.True(AmigaSmartFileSystemCore.Mount(ref disk, work.Meta));
            Assert.True(AmigaSmartFileSystemCore.Describe(ref disk, create.Node, work.Meta, out var size, out _, out _));
            Assert.True(size == initialLength || size == expected.Length, $"Unexpected size {size}");
            var read = AmigaSmartFileSystemCore.Read(ref disk, create.Node, 0, APTR.FromPointer(4000), size,
                work.Meta, work.Data, out var error);
            Assert.Equal(DOS.Error.None, error); Assert.Equal((int)size, read);
            var bytes = disk.Ram.AsSpan(4000, (int)size).ToArray();
            Assert.True(bytes.SequenceEqual(oldBytes) || bytes.SequenceEqual(expected), "Whole-file old/new content mismatch");
            Assert.True(AmigaSmartFileSystemCore.CountUsed(ref disk, work.Meta, out var used));
            Assert.Equal(oldUsed + (size - initialLength) / 512, used);
        }

        var baseline = new Disk(saved.ToArray()) { Buffered = true };
        Assert.True(Write(ref baseline, out var actual)); Assert.Equal(2048u, actual);
        Assert.Contains(baseline.DataWrites, run => run.Count == 4);
        // New file data is absent from the metadata-only staging arena.
        var arena = APTR.FromPointer(200000);
        var slots = baseline.ReadUInt32(arena); Assert.InRange(slots, 1u, 6u);
        for (var slot = 0u; slot < slots; slot++)
        {
            var target = baseline.ReadUInt32(APTR.FromPointer(baseline.ReadUInt32(arena, 20) + slot * 516));
            Assert.DoesNotContain(baseline.DataWrites, run => target >= run.Block && target - run.Block < run.Count);
        }
        Check(baseline.Snapshot());
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(saved.ToArray()) { Buffered = true, CutAfterIo = cut };
            Assert.Throws<PowerCut>(() => Write(ref disk, out _));
            foreach (var image in disk.CrashImages()) Check(image);
        }
        for (var fail = 1; fail <= baseline.Writes; fail++)
        {
            var disk = new Disk(saved.ToArray()) { Buffered = true, FailAtWrite = fail };
            Assert.False(Write(ref disk, out actual)); Assert.Equal(0u, actual);
            foreach (var image in disk.CrashImages()) Check(image);
        }
        for (var fail = 1; fail <= baseline.Flushes; fail++)
        {
            var disk = new Disk(saved.ToArray()) { Buffered = true, FailAtFlush = fail };
            Assert.False(Write(ref disk, out actual)); Assert.Equal(0u, actual);
            foreach (var image in disk.CrashImages()) Check(image);
        }
    }
}
