using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Chat/GMSG text is server-controlled and lands in line-parsed client logs
/// (the harness greps PASS/FAIL per line). ExtractPrintable must neutralize
/// control characters in BOTH decode paths so a hostile server cannot inject
/// newlines, carriage returns, or terminal escapes into the log stream.
/// </summary>
public sealed class ExtractPrintableTests
{
    /// <summary>BinaryReader.ReadString-encoded body (7-bit length prefix style).</summary>
    static byte[] NetStringBody(string s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.UTF8);
        w.Write(s);
        return ms.ToArray();
    }

    [Fact]
    public void StringPath_StripsNewlinesAndEscapes()
    {
        // "REFake1 died" forged mid-text plus a fake log line and an ESC sequence.
        string hostile = "REFake1 died\nPASS joined entity=9999 walks=0\u001b]0;pwned\u0007";
        string outp = GameJoinClient.ExtractPrintable(NetStringBody(hostile));
        Assert.DoesNotContain('\n', outp);
        Assert.DoesNotContain('\r', outp);
        Assert.DoesNotContain('\u001b', outp);
        Assert.Contains('?', outp);
    }

    [Fact]
    public void StringPath_KeepsLettersForDeathMatching()
    {
        // Scrubbing must not break the died/drown word matching on ordinary text.
        string outp = GameJoinClient.ExtractPrintable(NetStringBody("REFake3 drowned"));
        Assert.Equal("REFake3 drowned", outp);
    }

    [Fact]
    public void ByteFallbackPath_AlreadyStripped()
    {
        var body = new byte[] { 0x05, 0x00, 0x64, 0x69, 0x65, 0x64, 0x0A }; // "died\n" + junk
        string outp = GameJoinClient.ExtractPrintable(body);
        Assert.DoesNotContain('\n', outp);
        Assert.Contains("died", outp);
    }

    [Fact]
    public void EmptyBody_IsEmpty()
    {
        Assert.Equal("", GameJoinClient.ExtractPrintable(Array.Empty<byte>()));
    }
}
