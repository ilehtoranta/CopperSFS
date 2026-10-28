using System.Buffers.Binary;
using System.Reflection;
using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private const uint InlineTrst = 0x54525354, InlineTrfa = 0x54524641;

    [Theory]
    [InlineData(120, 4, false, 512u, true)]  // 496 bytes: exactly fills the page.
    [InlineData(120, 4, true, 512u, false)]  // Next record starts on a new page.
    [InlineData(119, 4, true, 512u, false)]  // Next header starts at byte 508.
    [InlineData(119, 5, true, 512u, false)]  // Next header starts at byte 510.
    [InlineData(128, 1, false, 512u, false)] // Literal span crosses the page.
    [InlineData(120, 4, false, 256u, false)] // A 512-byte block is two sectors.
    public void TrstInlineAndPromotedJournalsReplayEveryCutAtFramingBoundaries(
        int literalWords, int pieces, bool secondRecord, uint sectorBytes, bool inline)
    {
        var initial = Image();
        var payload = InlinePayload(literalWords, pieces);
        var second = Enumerable.Repeat((byte)0xFF, Bytes).ToArray();
        var targets = secondRecord ? new uint[] { 40, 41 } : [40u];
        var images = secondRecord ? new[] { payload, second } : [payload];
        Disk New()
        {
            var disk = new Disk(initial.ToArray()) { Buffered = true, SectorBytesOverride = sectorBytes,
                WrittenSectors = [], FlushWriteCounts = [] };
            // Allocate the volatile dictionary before reflection copies the
            // platform value. Exceptions do not copy boxed ref arguments back;
            // the device and pending dictionary must remain shared on a cut.
            Assert.True(disk.WriteBlock(40, TrstWork().Data)); Assert.True(disk.Flush());
            disk.Writes = disk.Flushes = disk.IoEvents = 0;
            disk.WrittenSectors.Clear(); disk.FlushWriteCounts!.Clear();
            return disk;
        }
        bool Commit(ref Disk disk) => CommitInlineFixture(ref disk, targets, images);
        void Check(byte[] image, bool final = false)
        {
            var recovered = new Disk(image); var w = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref w));
            var result = recovered.Snapshot();
            var old = targets.All(t => result.AsSpan((int)t * Bytes, Bytes).SequenceEqual(initial.AsSpan((int)t * Bytes, Bytes)));
            var complete = targets.Select((t, i) => result.AsSpan((int)t * Bytes, Bytes).SequenceEqual(images[i])).All(v => v);
            Assert.True(complete || !final && old, "Journal must replay all records, including split fields and literals");
            Assert.Equal(TransactionOk, BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(4 * Bytes)));
        }
        var baseline = New();
        Assert.True(Commit(ref baseline)); Check(baseline.Snapshot(), true);
        var sectors = baseline.WrittenSectors!;
        var marker = sectors.Where(s => s.Block == 4).ToArray();
        Assert.Equal(2, marker.Length);
        Assert.Equal(inline ? InlineTrst : InlineTrfa, BinaryPrimitives.ReadUInt32BigEndian(marker[0].Data));
        Assert.Equal(TransactionOk, BinaryPrimitives.ReadUInt32BigEndian(marker[1].Data));
        var pages = sectors.Where(s => BinaryPrimitives.ReadUInt32BigEndian(s.Data) == InlineTrst).ToArray();
        Assert.Equal(secondRecord || literalWords == 128 ? 2 : 1, pages.Length);
        Assert.Equal(4, baseline.Flushes);
        foreach (var page in pages)
        {
            Assert.Equal(page.Block, BinaryPrimitives.ReadUInt32BigEndian(page.Data.AsSpan(8)));
            Assert.Equal(uint.MaxValue, Enumerable.Range(0, Bytes / 4).Aggregate(0u,
                (sum, i) => unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(page.Data.AsSpan(i * 4)))));
        }
        if (inline)
        {
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(pages[0].Data.AsSpan(12)));
            Assert.Equal(488, BinaryPrimitives.ReadUInt16BigEndian(pages[0].Data.AsSpan(16)));
            Assert.Equal(new[] { 0, 1, 1 + targets.Length, 2 + targets.Length }, baseline.FlushWriteCounts);
        }
        else
        {
            Assert.DoesNotContain(pages, p => p.Block == 4 || targets.Contains(p.Block));
            Assert.Equal(pages[0].Block, BinaryPrimitives.ReadUInt32BigEndian(marker[0].Data.AsSpan(12)));
            if (pages.Length == 2)
                Assert.Equal(pages[1].Block, BinaryPrimitives.ReadUInt32BigEndian(pages[0].Data.AsSpan(12)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(pages[^1].Data.AsSpan(12)));
        }
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = New(); disk.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() => Commit(ref disk));
            foreach (var saved in disk.CrashImages()) Check(saved);
        }
        for (var failure = 1; failure <= baseline.Writes; failure++)
        {
            var disk = New(); disk.FailAtWrite = failure;
            Assert.False(Commit(ref disk));
            foreach (var saved in disk.CrashImages()) Check(saved);
        }
        for (var failure = 1; failure <= baseline.Flushes; failure++)
        {
            var disk = New(); disk.FailAtFlush = failure;
            Assert.False(Commit(ref disk));
            foreach (var saved in disk.CrashImages()) Check(saved);
        }
    }

    [Fact]
    public void TrstInlinePublicationFlushesUnpublishedPayloadBeforePublishingItsMarker()
    {
        var seed = GroupSeed(8192);
        var payload = Enumerable.Range(0, 2048).Select(i => (byte)(i * 29 + 7)).ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, WrittenSectors = [], FlushWriteCounts = [] };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        Assert.True(GroupWrite(ref disk, seed.Node, 0, payload));
        Assert.DoesNotContain(disk.WrittenSectors!, s => s.Block == 4);
        var payloadCount = disk.WrittenSectors!.Count;
        Assert.Equal(4, payloadCount);
        disk.FlushWriteCounts!.Clear();
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        Assert.Equal(payloadCount, disk.FlushWriteCounts![0]);
        Assert.Equal(payloadCount + 1, disk.FlushWriteCounts![1]);
        Assert.Equal(4u, disk.WrittenSectors![payloadCount].Block);
        Assert.Equal(InlineTrst, BinaryPrimitives.ReadUInt32BigEndian(disk.WrittenSectors![payloadCount].Data));
        Assert.DoesNotContain(disk.WrittenSectors!, s => BinaryPrimitives.ReadUInt32BigEndian(s.Data) == InlineTrfa);
        var final = seed.Bytes.ToArray(); payload.CopyTo(final, 0);
        var reboot = new Disk(disk.Snapshot());
        Assert.Equal(final, GroupRead(ref reboot, seed.Node));
    }

    private static byte[] InlinePayload(int literals, int pieces)
    {
        var data = new byte[Bytes];
        var word = 0;
        for (var piece = 0; piece < pieces; piece++)
        {
            var take = literals / pieces + (piece < literals % pieces ? 1 : 0);
            for (var i = 0; i < take; i++, word++)
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(word * 4), (uint)(word + 1));
            word += (128 - literals) / pieces + (piece < (128 - literals) % pieces ? 1 : 0);
        }
        Assert.Equal(128, word);
        return data;
    }

    // Exercise exact stream framing without making filesystem layout determine
    // the compressor's lengths. Public group/data tests cover actual file writes.
    private static bool CommitInlineFixture(ref Disk disk, uint[] targets, byte[][] images)
    {
        var arena = APTR.FromPointer(200000);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref disk, arena, 16384, Bytes));
        var work = TrstWork(); work.RootContainer = 2; work.BitmapBase = 9;
        for (var i = 0; i < targets.Length; i++)
        {
            images[i].CopyTo(disk.Ram, (int)work.Data.Raw);
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk, arena, targets[i], work.Data));
        }
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        object boxedWork = work;
        typeof(AmigaSmartFileSystemWriteCore.Work).GetField("BlockBytes", fields)!.SetValue(boxedWork, 512u);
        typeof(AmigaSmartFileSystemWriteCore.Work).GetField("BlockCount", fields)!.SetValue(boxedWork, disk.BlockCount);
        typeof(AmigaSmartFileSystemWriteCore.Work).GetField("ReservedBlocks", fields)!.SetValue(boxedWork, disk.ReservedBlocks);
        var core = typeof(AmigaSmartFileSystemWriteCore);
        var type = core.GetNestedType("RedoPlatform`1", BindingFlags.NonPublic)!.MakeGenericType(typeof(Disk));
        var staged = Activator.CreateInstance(type)!;
        type.GetField("Inner", fields)!.SetValue(staged, disk);
        type.GetField("Arena", fields)!.SetValue(staged, arena);
        type.GetField("Buffer", fields)!.SetValue(staged, APTR.FromPointer(disk.ReadUInt32(arena, 20)));
        type.GetField("Stride", fields)!.SetValue(staged, Bytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes);
        type.GetField("Count", fields)!.SetValue(staged, (uint)targets.Length);
        object[] args = [disk, boxedWork, staged];
        try
        {
            return (bool)core.GetMethod("CommitTrst", BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(Disk)).Invoke(null, args)!;
        }
        catch (TargetInvocationException e) when (e.InnerException is PowerCut)
        { throw e.InnerException; }
        finally { disk = (Disk)args[0]; }
    }
}
