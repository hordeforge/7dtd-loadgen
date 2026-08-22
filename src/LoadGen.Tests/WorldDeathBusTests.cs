using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// WorldDeathBus is the telnet-kill side channel that triggers respawn: when
/// the pressure task issues "kill PlayerName", the matching bot must observe
/// exactly one consumable death event (docstring contract on the class).
/// The store is process-global static state, so every test uses a unique bot
/// name to stay independent of execution order and parallel cohorts.
/// </summary>
public sealed class WorldDeathBusTests
{
    static string UniqueName(string tag) => $"wdt-{tag}-{Guid.NewGuid():N}";

    [Fact]
    public void NotifyThenConsume_TrueOnce_WithTimestamp()
    {
        var name = UniqueName("once");
        WorldDeathBus.NotifyKilled(name);

        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(WorldDeathBus.TryConsumeKill(name, out var killedAtUtcMs));
        Assert.InRange(killedAtUtcMs, before - 5000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1);

        // Consumed events must not re-trigger respawn on the next poll.
        Assert.False(WorldDeathBus.TryConsumeKill(name, out _));
    }

    [Fact]
    public void Match_IsCaseInsensitive_AndTrimsWhitespace()
    {
        var name = UniqueName("case");
        WorldDeathBus.NotifyKilled($"  {name} ");

        // Telnet listplayers output carries padded/cased names; the lookup
        // must canonicalize both sides.
        Assert.True(WorldDeathBus.TryConsumeKill(name.ToUpperInvariant(), out _));
        Assert.False(WorldDeathBus.TryConsumeKill(name, out _)); // already consumed above
    }

    [Fact]
    public void UnknownName_False_AndZeroTimestamp()
    {
        Assert.False(WorldDeathBus.TryConsumeKill(UniqueName("unknown"), out var at));
        Assert.Equal(0, at);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankNames_NoThrow_AndNeverMatch(string blank)
    {
        WorldDeathBus.NotifyKilled(blank);

        // The guard rejects blanks on both paths: nothing recorded, nothing
        // consumable (a telnet glitch must not respawn an arbitrary bot).
        Assert.False(WorldDeathBus.TryConsumeKill(blank, out _));
    }
}
