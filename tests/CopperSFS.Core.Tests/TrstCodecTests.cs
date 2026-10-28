using System.Buffers.Binary;
using Amiga;

namespace CopperSFS.Tests;

public sealed class TrstCodecTests
{
    private struct Memory(int length) : IAmigaGuestMemory
    {
        public readonly byte[] Bytes = Enumerable.Repeat((byte)0xA5, length).ToArray();
        public int LongReads;
        public byte ReadUInt8(APTR a, int offset = 0) => Bytes[(int)a.Raw + offset];
        public ushort ReadUInt16(APTR a, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)a.Raw + offset));
        public uint ReadUInt32(APTR a, int offset = 0)
        { LongReads++; return BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)a.Raw + offset)); }
        public void WriteUInt8(APTR a, int offset, byte value) => Bytes[(int)a.Raw + offset] = value;
        public void WriteUInt16(APTR a, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)a.Raw + offset), value);
        public void WriteUInt32(APTR a, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)a.Raw + offset), value);
        public void Clear(APTR a, uint length) => Bytes.AsSpan((int)a.Raw, (int)length).Clear();
        public void Copy(APTR a, APTR b, uint length) => Bytes.AsSpan((int)a.Raw, (int)length).CopyTo(Bytes.AsSpan((int)b.Raw));
        public bool IsMapped(APTR a, uint length) => a.Raw <= Bytes.Length && length <= Bytes.Length - a.Raw;
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void PlansRoundTripEveryCompressionModeAndReadEachWordOnce(int bytes)
    {
        foreach (var pattern in new[] { "same", "zero", "ones", "literal", "alternating", "runs", "random" })
        {
            var p = new Memory(bytes * 3 + 128);
            var before = APTR.FromPointer(32); var after = APTR.FromPointer((uint)(bytes + 64));
            var plan = APTR.FromPointer((uint)(2 * bytes + 96));
            var random = new Random(171);
            for (var i = 0; i < bytes / 4; i++)
            {
                var old = (uint)random.NextInt64(1, uint.MaxValue);
                var kind = pattern switch { "same" => 1, "zero" => 0, "ones" => 2, "literal" => 3,
                    "alternating" => i % 4, "runs" => (i / 65) % 4, _ => random.Next(4) };
                var value = kind switch { 0 => 0u, 1 => old, 2 => uint.MaxValue, _ => old ^ 0x5A5A5A5A };
                p.WriteUInt32(before, i * 4, old); p.WriteUInt32(after, i * 4, value);
            }
            var original = p.Bytes.AsSpan((int)before.Raw, bytes).ToArray();
            var final = p.Bytes.AsSpan((int)after.Raw, bytes).ToArray();
            var runs = AmigaSmartFileSystemTrstCodec.Plan(ref p, before, after, (uint)bytes, plan, out var length);
            Assert.Equal(bytes / 2, p.LongReads);
            Assert.InRange(runs, 1u, (uint)bytes / 4);
            Assert.InRange(length, 1u, ushort.MaxValue);
            // Decode the plan independently using SFS's four wire-format modes.
            var decoded = original.ToArray(); var offset = 0; var payload = 0u;
            for (var run = 0u; run < runs; run++)
            {
                var code = p.ReadUInt8(plan, (int)run); var take = ((code & 63) + 1) * 4;
                var target = decoded.AsSpan(offset, take); payload++;
                switch (code >> 6)
                {
                    case 0: target.Clear(); break;
                    case 1: break;
                    case 2: target.Fill(255); break;
                    case 3: final.AsSpan(offset, take).CopyTo(target); payload += (uint)take; break;
                }
                offset += take;
            }
            Assert.Equal(bytes, offset); Assert.Equal(length, payload); Assert.Equal(final, decoded);
            Assert.Equal(original, p.Bytes.AsSpan((int)before.Raw, bytes).ToArray());
            Assert.Equal(final, p.Bytes.AsSpan((int)after.Raw, bytes).ToArray());
            Assert.Equal(0xA5, p.Bytes[plan.Raw - 1]);
            Assert.All(p.Bytes.AsSpan((int)(plan.Raw + runs)).ToArray(), b => Assert.Equal(0xA5, b));
        }
    }

    [Fact]
    public void RunBoundariesMatchOriginalSfsCodes()
    {
        var p = new Memory(1700); var before = APTR.FromPointer(16);
        var after = APTR.FromPointer(600); var plan = APTR.FromPointer(1200);
        // 63 unchanged, 64 zero, 1 all-ones: exact wire codes and payload size.
        for (var i = 0; i < 128; i++)
        { p.WriteUInt32(before, i * 4, 0x12345678); p.WriteUInt32(after, i * 4, i < 63 ? 0x12345678u : i < 127 ? 0u : uint.MaxValue); }
        Assert.Equal(3u, AmigaSmartFileSystemTrstCodec.Plan(ref p, before, after, 512, plan, out var length));
        Assert.Equal(3u, length);
        Assert.Equal(new byte[] { 0x7E, 0x3F, 0x80 }, p.Bytes.AsSpan((int)plan.Raw, 3).ToArray());
        // A 65-word unchanged run must split at 64, not wrap its six-bit length.
        for (var i = 0; i < 128; i++) p.WriteUInt32(after, i * 4, i < 65 ? 0x12345678u : 0u);
        Assert.Equal(3u, AmigaSmartFileSystemTrstCodec.Plan(ref p, before, after, 512, plan, out length));
        Assert.Equal(new byte[] { 0x7F, 0x40, 0x3E }, p.Bytes.AsSpan((int)plan.Raw, 3).ToArray());
    }
}
