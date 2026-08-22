using System.Text;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Telnet output is read as fixed-size TCP chunks, so multi-byte UTF-8
/// sequences land split across Decode() calls. Per-chunk GetString (the old
/// ReadAvailable behavior) emits U+FFFD at every such boundary, which corrupts
/// listplayers parsing and the kill-by-name fallback. These tests pin the
/// stateful decoder against the exact inputs that break naive chunking.
/// </summary>
public sealed class Utf8ChunkDecoderTests
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void TwoByteSequence_SplitAcrossChunks_DecodesClean()
    {
        // "é" = C3 A9; a TCP read can stop after C3.
        var d = new Utf8ChunkDecoder();
        var bytes = Utf8("é");
        Assert.Equal("é", d.Decode(bytes.AsSpan(0, 1)) + d.Decode(bytes.AsSpan(1)));
    }

    [Fact]
    public void ThreeByteSequence_SplitMidway_DecodesClean()
    {
        // "Zombie" + "ä" (C3 A4) + "rztin"; split after the first byte of "ä".
        var d = new Utf8ChunkDecoder();
        var bytes = Utf8("Zombieärztin");
        var joined = d.Decode(bytes.AsSpan(0, 7)) + d.Decode(bytes.AsSpan(7));
        Assert.Equal("Zombieärztin", joined);
    }

    [Fact]
    public void FourByteAstral_SplitEveryByte_StillRoundTrips()
    {
        // Zombie emoji U+1F9DF is F0 9F A7 9F: feed it one byte per call and
        // the decoder must emit exactly one char at the end (no U+FFFD).
        var d = new Utf8ChunkDecoder();
        var bytes = Utf8("\U0001F9DF");
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++) sb.Append(d.Decode(bytes.AsSpan(i, 1)));
        Assert.Equal("\U0001F9DF", sb.ToString());
        Assert.DoesNotContain("\uFFFD", sb.ToString());
    }

    [Fact]
    public void ChunkedStream_MatchesOneShotDecode()
    {
        // A realistic telnet burst: banner with non-ASCII names in listplayers,
        // chopped at arbitrary offsets like a 4096-byte socket read.
        string text = "Total of 3 in the game\r\n" +
                      "pltfmid=Local_Z\u00f6e, health=98\r\n" +
                      "pltfmid=Local_\U0001F9DFbot, health=100\r\n";
        var bytes = Utf8(text);
        var d = new Utf8ChunkDecoder();
        var sb = new StringBuilder();
        int off = 0;
        while (off < bytes.Length)
        {
            int n = Math.Min(5, bytes.Length - off);
            sb.Append(d.Decode(bytes.AsSpan(off, n)));
            off += n;
        }
        Assert.Equal(text, sb.ToString());
        Assert.DoesNotContain("\uFFFD", sb.ToString());
    }

    [Fact]
    public void InvalidBytes_FallBackPerByte_NotPerChunk()
    {
        // A lone invalid byte decodes to one replacement char wherever it
        // appears; valid neighbors around it survive.
        var d = new Utf8ChunkDecoder();
        var first = new byte[] { (byte)'a', 0xFF };
        var second = Utf8("b");
        Assert.Equal("a\uFFFDb", d.Decode(first) + d.Decode(second));
    }

    [Fact]
    public void Reset_DropsPendingSequence()
    {
        var d = new Utf8ChunkDecoder();
        var bytes = Utf8("é");
        Assert.Equal("", d.Decode(bytes.AsSpan(0, 1))); // lead byte held pending
        d.Reset();
        Assert.Equal("A", d.Decode(Utf8("A")));
    }
}
