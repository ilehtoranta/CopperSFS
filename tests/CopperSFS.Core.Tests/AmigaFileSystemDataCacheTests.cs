using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Fact]
    public void CleanCacheRandomReplacementsNeverReturnAnOlderOrIncompleteRange()
    {
        var p = new Disk(Image()); var cache = APTR.FromPointer(131072);
        var source = APTR.FromPointer(4000); var target = APTR.FromPointer(40000);
        Assert.True(AmigaFileSystemDataCache.Initialize(ref p, cache, Bytes, 8));
        var expected = new Dictionary<uint, byte[]>(); var random = new Random(0x534653);
        for (var step = 0; step < 2000; step++)
        {
            var block = (uint)random.Next(256); var count = (uint)random.Next(1, 21);
            if (step % 97 == 0) { AmigaFileSystemDataCache.Invalidate(ref p, cache); expected.Clear(); }
            else if (step % 3 == 0)
            {
                random.NextBytes(p.Ram.AsSpan((int)source.Raw, (int)count * Bytes));
                AmigaFileSystemDataCache.Store(ref p, cache, block, count, source);
                for (var i = 0u; i < count; i++) expected[block + i] = p.Ram.AsSpan((int)source.Raw + (int)i * Bytes, Bytes).ToArray();
            }
            else
            {
                Array.Fill(p.Ram, (byte)0xB5, (int)target.Raw, (int)count * Bytes);
                if (AmigaFileSystemDataCache.TryRead(ref p, cache, block, count, target))
                    for (var i = 0u; i < count; i++) Assert.Equal(expected[block + i], p.Ram.AsSpan((int)target.Raw + (int)i * Bytes, Bytes).ToArray());
                else Assert.All(p.Ram.AsSpan((int)target.Raw, (int)count * Bytes).ToArray(), b => Assert.Equal(0xB5, b));
            }
        }
    }
    [Theory]
    [InlineData(512u, 4u)]
    [InlineData(512u, 32u)]
    [InlineData(1024u, 16u)]
    [InlineData(4096u, 4u)]
    public void CleanCachePreservesPartialPagesAndNeverChangesDestinationOnPartialMiss(uint bytes, uint pages)
    {
        var p = new Disk(Image()); var cache = APTR.FromPointer(131072);
        var source = APTR.FromPointer(4000); var target = APTR.FromPointer(40000);
        var size = AmigaFileSystemDataCache.StorageBytes(bytes, pages);
        Array.Fill(p.Ram, (byte)0xAD, (int)cache.Raw - 1, (int)size + 2);
        Assert.True(AmigaFileSystemDataCache.Initialize(ref p, cache, bytes, pages));
        var expected = Enumerable.Range(0, (int)bytes * 5).Select(i => (byte)(i * 29 + i / (int)bytes)).ToArray();
        expected.CopyTo(p.Ram, (int)source.Raw);
        // Cross a page boundary, but leave both outside neighbors absent.
        AmigaFileSystemDataCache.Store(ref p, cache, 14, 5, source);
        Assert.True(AmigaFileSystemDataCache.TryRead(ref p, cache, 14, 5, target));
        Assert.Equal(expected, p.Ram.AsSpan((int)target.Raw, expected.Length).ToArray());
        Array.Fill(p.Ram, (byte)0xCE, (int)target.Raw, (int)bytes * 6);
        Assert.False(AmigaFileSystemDataCache.TryRead(ref p, cache, 14, 6, target));
        Assert.All(p.Ram.AsSpan((int)target.Raw, (int)bytes * 6).ToArray(), b => Assert.Equal(0xCE, b));
        Assert.False(AmigaFileSystemDataCache.TryRead(ref p, cache, 13, 1, target));
        Assert.Equal(0xAD, p.Ram[cache.Raw - 1]); Assert.Equal(0xAD, p.Ram[cache.Raw + size]);
        Assert.Equal(0, p.Writes); Assert.Equal(0, p.Flushes);
    }

    [Fact]
    public void CleanCacheReusedPagesSurviveProbationaryCollisionsAndInvalidationCannotResurrectData()
    {
        var p = new Disk(Image()); var cache = APTR.FromPointer(131072);
        var source = APTR.FromPointer(4000); var target = APTR.FromPointer(40000);
        Assert.True(AmigaFileSystemDataCache.Initialize(ref p, cache, Bytes, 4));
        for (var page = 0u; page < 4; page++)
        {
            Array.Fill(p.Ram, (byte)page, 4000, Bytes * 16);
            AmigaFileSystemDataCache.Store(ref p, cache, page * 16, 16, source);
        }
        Assert.True(AmigaFileSystemDataCache.TryRead(ref p, cache, 0, 16, target));
        Array.Fill(p.Ram, (byte)4, 4000, Bytes * 16);
        AmigaFileSystemDataCache.Store(ref p, cache, 64, 16, source);
        Assert.True(AmigaFileSystemDataCache.TryRead(ref p, cache, 0, 16, target));
        Assert.All(p.Ram.AsSpan(40000, Bytes * 16).ToArray(), b => Assert.Equal(0, b));
        AmigaFileSystemDataCache.Invalidate(ref p, cache);
        Assert.False(AmigaFileSystemDataCache.TryRead(ref p, cache, 0, 16, target));
        // A new generation must not retain validity bits from an older page.
        AmigaFileSystemDataCache.Store(ref p, cache, 0, 1, source);
        Assert.False(AmigaFileSystemDataCache.TryRead(ref p, cache, 0, 2, target));
        Assert.True(AmigaFileSystemDataCache.TryRead(ref p, cache, 0, 1, target));
        Assert.All(p.Ram.AsSpan(40000, Bytes).ToArray(), b => Assert.Equal(4, b));
        p.WriteUInt32(cache, AmigaFileSystemDataCache.Header.Generation, uint.MaxValue);
        AmigaFileSystemDataCache.Store(ref p, cache, 16, 1, source);
        AmigaFileSystemDataCache.Invalidate(ref p, cache);
        Assert.False(AmigaFileSystemDataCache.TryRead(ref p, cache, 0, 1, target));
        Assert.False(AmigaFileSystemDataCache.TryRead(ref p, cache, 16, 1, target));
    }

    [Fact]
    public void CleanCacheDisabledOrOversizedRequestsDoNotTouchMemoryOrPerformIo()
    {
        var p = new Disk(Image()); var cache = APTR.FromPointer(131072); var data = APTR.FromPointer(4000);
        Assert.True(AmigaFileSystemDataCache.Initialize(ref p, cache, Bytes, 4));
        var before = p.Ram.ToArray();
        AmigaFileSystemDataCache.Store(ref p, APTR.Null, 1, 1, data);
        AmigaFileSystemDataCache.Store(ref p, cache, 1, 65, data);
        Assert.False(AmigaFileSystemDataCache.TryRead(ref p, APTR.Null, 1, 1, data));
        Assert.False(AmigaFileSystemDataCache.TryRead(ref p, cache, uint.MaxValue, 2, data));
        Assert.Equal(before, p.Ram);
        Assert.Equal(0, p.Writes); Assert.Equal(0, p.Flushes);
        Assert.Equal(262688u, AmigaFileSystemDataCache.StorageBytes(512, 32));
    }
}
