using System.Buffers.Binary;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Allocation pins for the two wire-path hot spots. Every bot builds one
/// outbound frame per action (~20-40x/s each) and parses one list per received
/// datagram, so steady-state allocations must stay at exactly the returned
/// payload; streams/writers/lists must come from the per-thread scratch.
/// </summary>
public sealed class PackageCodecAllocationTests
{
    static byte[] BuildRelFrame() =>
        PackageCodec.BuildEntityRelPosAndRot(
            8, entityId: 7, dx: 3, dy: 0, dz: -4,
            rotX: 0, rotY: 128, rotZ: 0,
            onGround: true, updateSteps: 1);

    [Fact]
    public void FrameChannelPackage_SteadyState_AllocatesAtMostTheFrameArray()
    {
        // Warm the thread-static scratch stream/writer (and any lazy
        // BinaryWriter buffers) before measuring.
        _ = BuildRelFrame();

        const int iterations = 200;
        int frameLen = BuildRelFrame().Length; // 15-byte envelope + 20-byte body

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            _ = BuildRelFrame();
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        // Exactly one fresh byte[frameLen] per call is required (LiteNetLib
        // retains ReliableOrdered payloads, so pooling is forbidden). The upper
        // bound leaves room only for allocator header accounting; a single
        // leaked MemoryStream or BinaryWriter per call (~100+ B) would blow it.
        Assert.InRange(delta, iterations * frameLen, iterations * (frameLen + 64));
    }

    [Fact]
    public void ParseChannelPayload_SteadyState_AllocatesOnlyBodyCopies()
    {
        // One uncompressed frame carrying two inner packages.
        byte[] data = BuildTwoPackageFrame();
        int bodyBytes = 3 + 4;

        // Warm the thread-static result list.
        _ = PackageCodec.ParseChannelPayload(data);

        const int iterations = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            _ = PackageCodec.ParseChannelPayload(data);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        // Bodies must stay independent copies; nothing else (notably the
        // result List) may allocate per call beyond allocator-header slack.
        Assert.InRange(delta, iterations * bodyBytes, iterations * (bodyBytes + 96));
    }

    [Fact]
    public void ParseChannelPayload_ResultIsValidUntilNextParseOnSameThread()
    {
        // Pins the reuse contract: bodies survive into the next call window,
        // only the list identity is transient. Callers rely on copied arrays.
        var first = PackageCodec.ParseChannelPayload(BuildTwoPackageFrame());
        Assert.Equal(2, first.Count);
        var second = PackageCodec.ParseChannelPayload(BuildTwoPackageFrame());
        Assert.Equal((ushort)3, first[0].id);      // drained before next parse
        Assert.Equal(new byte[] { 9, 8, 7 }, first[0].body);
        Assert.Equal((ushort)4, second[1].id);
    }

    internal static byte[] BuildTwoPackageFrame()
    {
        // Inner package: [contentLen:i32][pkgId:u16][body...]
        static void Inner(List<byte> buf, ushort pkgId, byte[] body)
        {
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(word, body.Length + 2);
            buf.AddRange(word.ToArray());
            BinaryPrimitives.WriteUInt16LittleEndian(word, pkgId);
            buf.AddRange(word.Slice(0, 2).ToArray());
            buf.AddRange(body);
        }
        var payload = new List<byte>();
        Inner(payload, 3, new byte[] { 9, 8, 7 });
        Inner(payload, 4, new byte[] { 5, 4, 3, 2 });
        // Outer frame: [channel:1][payloadSize:i32][comp:1][enc:1][count:u16]
        var frame = new List<byte> { 0 };
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(word, payload.Count);
        frame.AddRange(word.ToArray()); // Slice(0,4): the span is exactly 4 long
        frame.Add(0); // not compressed
        frame.Add(0); // not encrypted
        BinaryPrimitives.WriteUInt16LittleEndian(word, 2);
        frame.AddRange(word.Slice(0, 2).ToArray());
        frame.AddRange(payload);
        return frame.ToArray();
    }
}
