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
}
