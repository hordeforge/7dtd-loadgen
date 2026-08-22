using System.Buffers.Binary;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// L10: ParseChannelPayload is the one decoder of raw, untrusted server bytes.
/// These exercise crafted hostile frames and a byte fuzz loop to assert the
/// hardening (overflow/OOB-BlockCopy guards) holds and no exception escapes.
/// </summary>
public sealed class PackageCodecFuzzTests
{
    // Outer frame: [channel:1][payloadSize:int32][compressed:1][encrypted:1][count:uint16][payload...]
    static byte[] Frame(byte[] payload, int? payloadSizeOverride, byte compressed, byte encrypted, ushort count)
    {
        var buf = new List<byte> { 0 }; // channel
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(word, payloadSizeOverride ?? payload.Length);
        buf.AddRange(word.ToArray());
        buf.Add(compressed);
        buf.Add(encrypted);
        BinaryPrimitives.WriteUInt16LittleEndian(word, count);
        buf.AddRange(word.Slice(0, 2).ToArray());
        buf.AddRange(payload);
        return buf.ToArray();
    }

    // Inner package: [contentLen:int32][pkgId:uint16][body...]. contentLen counts pkgId+body.
    static byte[] InnerPackage(int contentLen, ushort pkgId, byte[] body)
    {
        var buf = new List<byte>();
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(word, contentLen);
        buf.AddRange(word.ToArray());
        BinaryPrimitives.WriteUInt16LittleEndian(word, pkgId);
        buf.AddRange(word.Slice(0, 2).ToArray());
        buf.AddRange(body);
        return buf.ToArray();
    }

    [Fact]
    public void ContentLen_IntMaxValue_WithShortPayload_YieldsNothing_NoThrow()
    {
        var payload = InnerPackage(int.MaxValue, 7, new byte[] { 1, 2, 3, 4 });
        var frame = Frame(payload, null, compressed: 0, encrypted: 0, count: 1);
        var result = PackageCodec.ParseChannelPayload(frame);
        Assert.Empty(result); // guard breaks before any OOB BlockCopy
    }

    [Fact]
    public void ContentLen_Negative_YieldsNothing_NoThrow()
    {
        var payload = InnerPackage(-1, 7, new byte[] { 1, 2, 3, 4 });
        var frame = Frame(payload, null, compressed: 0, encrypted: 0, count: 1);
        var result = PackageCodec.ParseChannelPayload(frame);
        Assert.Empty(result); // contentLen < 2 guard trips
    }

    [Fact]
    public void PayloadSize_OversizedBeyondBuffer_YieldsNothing_NoThrow()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var frame = Frame(payload, payloadSizeOverride: 100_000, compressed: 0, encrypted: 0, count: 1);
        var result = PackageCodec.ParseChannelPayload(frame);
        Assert.Empty(result); // o + payloadSize > data.Length guard trips
    }

    [Fact]
    public void CountMax_WithTinyPayload_StopsEarly_NoThrow()
    {
        // Advertise 65535 inner packages but supply almost no bytes: the
        // per-iteration bound (po + 6 <= length) must stop the loop safely.
        var payload = new byte[] { 5, 0, 0, 0, 9, 0, 1 }; // one plausible-ish start then truncation
        var frame = Frame(payload, null, compressed: 0, encrypted: 0, count: 65535);
        var ex = Record.Exception(() => PackageCodec.ParseChannelPayload(frame));
        Assert.Null(ex);
    }

    [Fact]
    public void ByteFuzz_NoUnexpectedExceptionEscapes()
    {
        var rng = new Random(0xC0FFEE);
        for (int iter = 0; iter < 50_000; iter++)
        {
            int len = rng.Next(0, 64);
            var data = new byte[len];
            rng.NextBytes(data);
            try
            {
                var result = PackageCodec.ParseChannelPayload(data);
                Assert.NotNull(result);
            }
            catch (Exception ex)
            {
                // ParseChannelPayload is contracted to swallow malformed input and
                // return a (possibly empty) list. Any escaping exception is a bug.
                Assert.Fail($"ParseChannelPayload threw on fuzz input len={len}: " +
                            $"{ex.GetType().Name}: {ex.Message} bytes={Convert.ToHexString(data)}");
            }
        }
    }

    [Fact]
    public void CompressedFrame_RawDeflate_DecodesBodies()
    {
        // Noemax writes raw DEFLATE (no zlib header); TryInflate must resolve it
        // on the first attempt and the bodies must survive the span-based parse.
        byte[] body = { 1, 2, 3, 4, 5 };
        byte[] inner = InnerPackage(contentLen: body.Length + 2, pkgId: 7, body);
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var ds = new System.IO.Compression.DeflateStream(
                ms, System.IO.Compression.CompressionLevel.SmallestSize))
                ds.Write(inner, 0, inner.Length);
            compressed = ms.ToArray();
        }
        var frame = Frame(compressed, null, compressed: 1, encrypted: 0, count: 1);

        var pkgs = PackageCodec.ParseChannelPayload(frame);

        Assert.Single(pkgs);
        Assert.Equal((ushort)7, pkgs[0].id);
        Assert.Equal(body, pkgs[0].body);
    }

    [Fact]
    public void UncompressedFrame_BodiesMatchSourceBytes()
    {
        // The uncompressed path parses straight from the receive buffer; each
        // body must still be an independent copy (callers own their arrays).
        byte[] bodyA = { 9, 8, 7 };
        byte[] bodyB = { 5, 4, 3, 2 };
        var payload = new List<byte>();
        payload.AddRange(InnerPackage(bodyA.Length + 2, 3, bodyA));
        payload.AddRange(InnerPackage(bodyB.Length + 2, 4, bodyB));
        var frame = Frame(payload.ToArray(), null, compressed: 0, encrypted: 0, count: 2);

        var pkgs = PackageCodec.ParseChannelPayload(frame);

        Assert.Equal(2, pkgs.Count);
        Assert.Equal((ushort)3, pkgs[0].id);
        Assert.Equal(bodyA, pkgs[0].body);
        Assert.Equal((ushort)4, pkgs[1].id);
        Assert.Equal(bodyB, pkgs[1].body);
    }
}
