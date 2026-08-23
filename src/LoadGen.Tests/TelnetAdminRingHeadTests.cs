using System.Text;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// The ReadAvailable ring keeps the last 4000 chars of the telnet buffer; the
/// cut can land between the halves of a surrogate pair (chat text with emoji
/// at the cut point). DropUnpairedRingHead must drop either unpaired half so
/// the retained window never starts with ill-formed UTF-16.
/// </summary>
public sealed class TelnetAdminRingHeadTests
{
    // U+1F9DF zombie emoji = surrogate pair D83E DDDF.
    const string Emoji = "\U0001F9DF";

    [Fact]
    public void LeadingLoneLowSurrogate_IsDropped()
    {
        // Cut landed between lead and trail: window starts with the trail half.
        var buf = new StringBuilder();
        buf.Append(Emoji[1]).Append("Zombieärztin");
        TelnetAdmin.DropUnpairedRingHead(buf);
        Assert.Equal("Zombieärztin", buf.ToString());
    }

    [Fact]
    public void LoneHighSurrogateAtEndOfWindow_IsDropped()
    {
        var buf = new StringBuilder();
        buf.Append(Emoji[0]);
        TelnetAdmin.DropUnpairedRingHead(buf);
        Assert.Equal("", buf.ToString());
    }

    [Fact]
    public void CompleteLeadingPair_IsKept()
    {
        var buf = new StringBuilder();
        buf.Append(Emoji).Append(" ok");
        TelnetAdmin.DropUnpairedRingHead(buf);
        Assert.Equal(Emoji + " ok", buf.ToString());
    }

    [Fact]
    public void OrdinaryText_IsNeverTouched()
    {
        var buf = new StringBuilder();
        buf.Append("Zöé \u00e9");
        TelnetAdmin.DropUnpairedRingHead(buf);
        Assert.Equal("Zöé \u00e9", buf.ToString());
    }

    [Fact]
    public void EmptyBuffer_IsNoOp()
    {
        var buf = new StringBuilder();
        TelnetAdmin.DropUnpairedRingHead(buf);
        Assert.Equal("", buf.ToString());
    }
}
