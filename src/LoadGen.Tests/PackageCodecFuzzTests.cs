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

    // L10b: the six body parsers consume bytes extracted from hostile frames.
    // Call sites wrap them in catch-all handlers, so the codec-level contract
    // is: return decoded fields, or throw one of the documented truncation /
    // count exceptions. Anything else escaping is a bug.
    static readonly HashSet<Type> AllowedParserExceptions = new()
    {
        typeof(EndOfStreamException),   // truncated body
        typeof(FormatException),        // 7-bit string length prefix with too many bytes
        typeof(IOException),            // ReadString decoded a negative string length
        typeof(OverflowException),      // negative mapping count -> negative array size
        typeof(OutOfMemoryException),   // huge mapping count allocated before any bound exists
                                        // (known gap: untrusted int drives the allocation;
                                        // flagged for sec-review, not blessed here)
    };

    static void InvokeAllBodyParsers(byte[] body, int iter)
    {
        try { _ = PackageCodec.ParsePackageIdsBody(body); }
        catch (Exception ex) when (AllowedParserExceptions.Contains(ex.GetType())) { }

        try { _ = PackageCodec.ParseLoginAnswerBody(body); }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException or IOException) { }

        try { _ = PackageCodec.ParsePlayerDeniedBody(body); }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException or IOException) { }

        try { _ = PackageCodec.ParseSpawnedBody(body); }
        catch (Exception ex) when (ex is EndOfStreamException) { }

        // Contracted sentinel path: bodies under the 30-byte minimum must
        // return id 0, not throw. Longer bodies decode or throw EndOfStream
        // on a truncated useQ=true tail (callers guard that branch).
        if (body.Length < 30)
            Assert.Equal((0, 0f, 0f, 0f, false), PackageCodec.ParsePosAndRotBody(body));
        else
        {
            try { _ = PackageCodec.ParsePosAndRotBody(body); }
            catch (Exception ex) when (ex is EndOfStreamException) { }
        }

        try { _ = PackageCodec.ParseAliveFlagsBody(body); }
        catch (Exception ex) when (ex is EndOfStreamException) { }

        // Decompressor must answer a clean bool for any garbage buffer, never
        // throw, and hand back null exactly when it reports failure. Sampled
        // rather than every iteration: each attempt constructs two native
        // decompression streams, which dominates suite runtime otherwise.
        if (iter % 20 == 0)
        {
            byte[]? inflated = null;
            bool ok = false;
            var rex = Record.Exception(() => ok = PackageCodec.TryInflate(body, out inflated));
            Assert.Null(rex);
            if (!ok)
                Assert.Null(inflated);
        }
    }

    [Fact]
    public void BodyParsers_MalformedBodies_OnlyContractedExceptionsEscape()
    {
        // Seeds: structurally plausible starts (valid field prefixes) that get
        // truncated/mutated, so the fuzzer explores around real layouts instead
        // of pure noise.
        byte[] packageIdsHead =
        {
            1, 3, 0, 0, 0, 10, 0, 0, 0, 14, 0, 0, 0, // version
            2, 0, 0, 0,                              // mapping count
            4, 78, 97, 109, 101,                     // "Name"
            4, 84, 121, 112, 101,                    // "Type"
            0, 0,                                    // useEac=false, hasHost=false
        };
        byte[] loginAnswerSeed = { 1, 5, 104, 101, 108, 108, 111 }; // true, "hello"
        byte[] deniedSeed = { 3, 0, 0, 0, 1, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 2, 98, 121 }; // reason/api/banUntil/"by"
        byte[][] seeds = { packageIdsHead, loginAnswerSeed, deniedSeed, Array.Empty<byte>() };

        var rng = new Random(0xB0D1E5);
        for (int iter = 0; iter < 10_000; iter++)
        {
            byte[] src = seeds[rng.Next(seeds.Length)];
            int len = rng.Next(0, Math.Max(src.Length + 17, 33));
            var data = new byte[len];
            if (src.Length > 0)
                Array.Copy(src, data, Math.Min(src.Length, len));
            rng.NextBytes(data.AsSpan(Math.Min(src.Length, len)));
            // Randomly corrupt one byte inside the copied seed region too.
            if (len > 0 && rng.Next(2) == 0)
                data[rng.Next(len)] ^= (byte)(1 << rng.Next(8));

            // Clamp only the PackageIds mapping-count field: a raw random u32
            // there makes the parser attempt multi-GB string[] allocations,
            // which is the documented unbounded-allocation gap below. Keeping
            // the loop under that ceiling keeps the suite fast; the extreme is
            // exercised once, deliberately, by the targeted test that follows.
            if (len >= 4)
            {
                int candidate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0));
                if (candidate > 65535)
                    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), rng.Next(0, 512));
            }

            var ex = Record.Exception(() => InvokeAllBodyParsers(data, iter));
            if (ex != null)
                Assert.Fail($"{ex.GetType().Name}: {ex.Message} bytes={Convert.ToHexString(data)}");
        }
    }

    [Fact]
    public void ParsePackageIds_HugeMappingCount_TerminatesWithoutProcessCrash()
    {
        // Pins the known gap: the mapping count is trusted before any bound,
        // so int.MaxValue attempts a >16GB string[]. Outcome is platform
        // dependent: Windows rejects the object size outright (OutOfMemory),
        // Linux overcommit reserves lazily and parsing hits end-of-stream.
        // Both are caught by the join client's package_ids_parse handler; the
        // contract under test is only that the call terminates with one of
        // them. If a future change bounds the count, assert that instead.
        var body = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0), int.MaxValue);
        var ex = Record.Exception(() => PackageCodec.ParsePackageIdsBody(body));
        Assert.True(ex is OutOfMemoryException or EndOfStreamException,
            $"unexpected exception type: {ex?.GetType().Name ?? "none"}");
    }

    [Fact]
    public void BuildThenParse_RoundTrip_PreservesFields()
    {
        // Pair assertion across the wire boundary: fields -> Build* -> framed
        // message -> ExtractBody -> Parse* must reproduce the exact inputs,
        // including empty / unicode / long strings and extreme numerics.
        var rng = new Random(0x1234ABCD);
        for (int iter = 0; iter < 2_000; iter++)
        {
            int entityId = rng.Next(int.MinValue, int.MaxValue);
            float x = NextFiniteFloat(rng), y = NextFiniteFloat(rng), z = NextFiniteFloat(rng);
            bool onGround = (rng.Next(2) == 1);
            ushort flags = (ushort)rng.Next(0, 1024);

            var spawned = PackageCodec.ExtractBody(PackageCodec.BuildPlayerSpawnedInWorld(
                packageId: 1, respawnReason: rng.Next(0, 8), posX: (int)x, posY: (int)y, posZ: (int)z, entityId));
            var (gotEid, gx, gy, gz) = PackageCodec.ParseSpawnedBody(spawned);
            Assert.Equal(entityId, gotEid);
            Assert.Equal((int)x, gx);
            Assert.Equal((int)y, gy);
            Assert.Equal((int)z, gz);

            var flagsBody = PackageCodec.ExtractBody(PackageCodec.BuildEntityAliveFlags(2, entityId, flags));
            var (feid, fflags) = PackageCodec.ParseAliveFlagsBody(flagsBody);
            Assert.Equal(entityId, feid);
            Assert.Equal(flags, fflags);

            bool useQ = (rng.Next(2) == 1);
            var posBody = PackageCodec.ExtractBody(PackageCodec.BuildEntityPosAndRot(
                3, entityId, x, y, z, rotX: x, rotY: y, rotZ: z, onGround, useQRotation: useQ));
            var (peid, px, py, pz, ponGround) = PackageCodec.ParsePosAndRotBody(posBody);
            Assert.Equal(entityId, peid);
            Assert.Equal(x, px);
            Assert.Equal(y, py);
            Assert.Equal(z, pz);
            Assert.Equal(onGround, ponGround);

            string payload = iter switch
            {
                0 => "",
                1 => "Z\u00f6me\U0001F9DF \u79c1",
                2 => new string('x', 70_000),
                _ => "msg-" + rng.Next(1 << 20),
            };
            var (_, loginData) = PackageCodec.ParseLoginAnswerBody(PackageCodec.ExtractBody(
                PackageCodec.BuildPlayerLoginAnswer(4, allowed: (rng.Next(2) == 1), payload)));
            Assert.Equal(payload, loginData);

            var maps = new[] { "NetPackageSimpleChat", "", "A" };
            var idsBody = PackageCodec.ExtractBody(PackageCodec.BuildPackageIdsServer(
                0, maps, new PackageCodec.VersionInfo(1, 3, 10, 14), serverUseEac: false));
            var (ver, gotMaps, _) = PackageCodec.ParsePackageIdsBody(idsBody);
            Assert.Equal(new PackageCodec.VersionInfo(1, 3, 10, 14), ver);
            Assert.Equal(maps, gotMaps);
        }
    }

    static float NextFiniteFloat(Random rng)
    {
        // Small-magnitude floats plus exact extremes: stays finite so equality
        // round-trips are meaningful (NaN/Inf handling is pinned separately).
        return rng.Next(8) switch
        {
            0 => float.Epsilon,
            1 => float.MinValue,
            2 => float.MaxValue,
            _ => (float)(rng.NextDouble() * 512.0 - 256.0),
        };
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
