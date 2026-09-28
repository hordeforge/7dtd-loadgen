using System.Text;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Wire strings carry a 7-bit length prefix the sender chooses. Every body
/// these come from is unauthenticated peer data, so the length is checked
/// against what the body can actually hold before anything is allocated:
/// BinaryReader.ReadString trusts the prefix and turns one crafted package
/// into a multi-gigabyte allocation.
/// </summary>
public sealed class PackageCodecStringBoundsTests
{
    static BinaryReader Reader(params byte[] body) =>
        new(new MemoryStream(body), Encoding.UTF8);

    /// <summary>7-bit length prefix, low group first, continuation bit set on
    /// every group but the last. Matches BinaryWriter.Write(string).</summary>
    static byte[] LengthPrefix(int len)
    {
        var bytes = new List<byte>();
        int v = len;
        do
        {
            int group = v & 0x7F;
            v >>= 7;
            bytes.Add((byte)(v != 0 ? group | 0x80 : group));
        } while (v != 0);
        return bytes.ToArray();
    }

    [Fact]
    public void ReadsAWellFormedString()
    {
        byte[] body = LengthPrefix(5).Concat(Encoding.UTF8.GetBytes("hello")).ToArray();
        using var r = Reader(body);
        Assert.Equal("hello", PackageCodec.ReadBoundedString(r, "test"));
    }

    [Fact]
    public void ReadsAMultiGroupLengthPrefix()
    {
        string text = new('x', 300);
        byte[] body = LengthPrefix(300).Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        using var r = Reader(body);
        Assert.Equal(text, PackageCodec.ReadBoundedString(r, "test"));
    }

    [Fact]
    public void RejectsALengthTheBodyCannotHold()
    {
        // Prefix claims 5 bytes, body carries 2: the classic truncated-body case.
        using var r = Reader(LengthPrefix(5).Concat(Encoding.UTF8.GetBytes("hi")).ToArray());
        Assert.Throws<InvalidDataException>(() => PackageCodec.ReadBoundedString(r, "test"));
    }

    [Fact]
    public void RejectsAnOversizedLengthBeforeAllocating()
    {
        // 0x7FFFFFFF: what a hostile peer sends. ReadString would try to build a
        // 2 GiB string from a 5-byte body.
        byte[] prefix = LengthPrefix(int.MaxValue);
        Assert.Equal(5, prefix.Length);
        using var r = Reader(prefix);
        Assert.Throws<InvalidDataException>(() => PackageCodec.ReadBoundedString(r, "test"));
    }

    [Fact]
    public void RejectsAnOverlongLengthPrefix()
    {
        // Six continuation groups: no int32 value, and the reader must not
        // shift its way past the range.
        using var r = Reader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 });
        Assert.Throws<InvalidDataException>(() => PackageCodec.ReadBoundedString(r, "test"));
    }

    [Fact]
    public void RejectsAFiveGroupPrefixThatSetsTheSignBit()
    {
        // Five in-range groups whose value is negative as an int32: the fourth
        // group alone (0x7F << 21) is 0xFE000000. The reader accumulated in int,
        // so len came out negative, passed the "len > limit" bound, and reached
        // ReadBytes as a negative count: ArgumentOutOfRangeException('count')
        // instead of the contracted InvalidDataException. Found by
        // PackageCodecFuzzTests.BodyParsers_MalformedBodies_OnlyContractedExceptionsEscape.
        byte[] prefix = { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };
        using var r = Reader(prefix);
        Assert.Throws<InvalidDataException>(() => PackageCodec.ReadBoundedString(r, "test"));
    }

    [Fact]
    public void RejectsATruncatedLengthPrefix()
    {
        using var r = Reader(new byte[] { 0x80 });
        Assert.Throws<InvalidDataException>(() => PackageCodec.ReadBoundedString(r, "test"));
    }

    [Fact]
    public void RejectsAnEmptyBody()
    {
        using var r = Reader(Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => PackageCodec.ReadBoundedString(r, "test"));
    }

    [Fact]
    public void ParsePackageIdsBody_RejectsAHugeStringPrefix()
    {
        // One mapping whose prefix claims int.MaxValue bytes. The mapping count
        // is bounded; the string length inside the body was not.
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            PackageCodec.WriteVersion(w, new PackageCodec.VersionInfo(1, 3, 1, 0));
            w.Write(1);                       // one mapping
            w.Write(LengthPrefix(int.MaxValue));
        }
        Assert.Throws<InvalidDataException>(
            () => PackageCodec.ParsePackageIdsBody(ms.ToArray()));
    }
}
