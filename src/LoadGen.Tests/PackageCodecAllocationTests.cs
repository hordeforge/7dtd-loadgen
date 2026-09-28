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
        // Pins the reuse contract: the list itself is transient (one per
        // thread), the body arrays are not. A second parse over a DIFFERENT
        // frame rewrites the reused list, so a body handed out as a slice of
        // the receive buffer would show up here as the second frame's bytes.
        var first = PackageCodec.ParseChannelPayload(BuildTwoPackageFrame());
        Assert.Equal(2, first.Count);
        byte[] firstBody = first[0].body;
        var firstBodyId = first[0].id;

        var second = PackageCodec.ParseChannelPayload(
            BuildTwoPackageFrame(idA: 41, bodyA: new byte[] { 1, 2 }, idB: 42, bodyB: new byte[] { 3 }));

        // List identity is the reuse, by contract.
        Assert.Same(first, second);
        Assert.Equal((ushort)41, second[0].id);
        // The first parse's body is an independent copy: still the first frame's.
        Assert.Equal((ushort)3, firstBodyId);
        Assert.Equal(new byte[] { 9, 8, 7 }, firstBody);
        Assert.NotSame(firstBody, second[0].body);
    }

    internal static byte[] BuildTwoPackageFrame(
        ushort idA = 3, byte[]? bodyA = null, ushort idB = 4, byte[]? bodyB = null)
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
        Inner(payload, idA, bodyA ?? new byte[] { 9, 8, 7 });
        Inner(payload, idB, bodyB ?? new byte[] { 5, 4, 3, 2 });
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
