using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Admin-command guards: listplayers tokens are server-controlled text that is
/// replayed into level-0 telnet commands (kill), so a crafted row must never
/// become a crafted command (threat model R3). IsSafeCommandToken allowlists
/// the token shape; IsSingleLineCommand rejects control characters at the one
/// sink every outbound command passes through.
/// </summary>
public sealed class TelnetAdminCommandGuardTests
{
    [Theory]
    [InlineData("REFake171", true)]
    [InlineData("76561198000000001", true)]
    [InlineData("eos_0f1e2d3c4b5a", true)]
    [InlineData("BotPoi_4k.v2", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("line1\nkickall", false)]
    [InlineData("cr\rinjected", false)]
    [InlineData("tab\tsep", false)]
    [InlineData("semi;colon", false)]
    [InlineData("pipe|cmd", false)]
    [InlineData("quote\"", false)]
    [InlineData("back\\slash", false)]
    [InlineData("nul\0byte", false)]
    [InlineData("del\x7fchar", false)]
    public void SafeCommandToken_AllowlistDecides(string value, bool ok)
        => Assert.Equal(ok, TelnetAdmin.IsSafeCommandToken(value));

    [Theory]
    [InlineData("kill REFake171", true)]
    [InlineData("spawnentity 17 zombieBoe", true)]
    [InlineData("give 12 thrownDynamite 3", true)]
    [InlineData("listplayers", true)]
    [InlineData("kill a\nkickall", false)]
    [InlineData("kill a\r\nban all", false)]
    [InlineData("\u0000", false)]
    [InlineData("\u007f", false)]
    public void SingleLineCommand_GuardRejectsControlCharacters(string cmd, bool ok)
        => Assert.Equal(ok, TelnetAdmin.IsSingleLineCommand(cmd));
}
