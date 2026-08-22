using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// CLI trust-boundary parsers for --mode / --death. A rejected value must be
/// loud at the parse layer (the caller keeps the previous setting), and the
/// documented alias sets decide which pressure profile actually runs, so a
/// silently dropped alias would weaken a benchmark without any error.
/// </summary>
public sealed class CliParserTests
{
    [Theory]
    [InlineData("wander", ActionLoop.BotMode.Wander)]
    [InlineData("MIXED", ActionLoop.BotMode.Mixed)]
    [InlineData("chatty", ActionLoop.BotMode.Chatty)]
    [InlineData("Patrol", ActionLoop.BotMode.Patrol)]
    public void ModeNames_ParseCaseInsensitive(string s, ActionLoop.BotMode expected)
    {
        Assert.True(ActionLoop.TryParseMode(s, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    public void BadMode_Rejected_WithWanderFallback(string s)
    {
        Assert.False(ActionLoop.TryParseMode(s, out var mode));
        Assert.Equal(ActionLoop.BotMode.Wander, mode);
    }

    [Theory]
    [InlineData("none", ActionLoop.DeathMethod.None)]
    [InlineData("natural", ActionLoop.DeathMethod.None)]
    [InlineData("world", ActionLoop.DeathMethod.None)]
    [InlineData("server", ActionLoop.DeathMethod.None)]
    [InlineData("live", ActionLoop.DeathMethod.None)]
    [InlineData("drown", ActionLoop.DeathMethod.Drown)]
    [InlineData("drown_fatal", ActionLoop.DeathMethod.Drown)]
    [InlineData("water", ActionLoop.DeathMethod.Drown)]
    [InlineData("suicide", ActionLoop.DeathMethod.Suicide)]
    [InlineData("killself", ActionLoop.DeathMethod.Suicide)]
    [InlineData("killed", ActionLoop.DeathMethod.Killed)]
    [InlineData("kill", ActionLoop.DeathMethod.Killed)]
    [InlineData("external", ActionLoop.DeathMethod.Killed)]
    [InlineData("random", ActionLoop.DeathMethod.Random)]
    [InlineData("any", ActionLoop.DeathMethod.Random)]
    public void DeathAliases_MapToDocumentedMethods(string s, ActionLoop.DeathMethod expected)
    {
        Assert.True(ActionLoop.TryParseDeath(s, out var death));
        Assert.Equal(expected, death);
    }

    [Fact]
    public void DeathParse_IsCaseInsensitive()
    {
        Assert.True(ActionLoop.TryParseDeath("WATER", out var death));
        Assert.Equal(ActionLoop.DeathMethod.Drown, death);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("exploded")]
    public void BadDeath_Rejected_WithNoneFallback(string s)
    {
        Assert.False(ActionLoop.TryParseDeath(s, out var death));
        Assert.Equal(ActionLoop.DeathMethod.None, death);
    }
}
