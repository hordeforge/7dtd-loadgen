using System.Text;
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
    public void NotifyThenConsume_TrueOnce_WithMonotonicStamp()
    {
        var name = UniqueName("once");
        long before = Environment.TickCount64;
        WorldDeathBus.NotifyKilled(name);

        // Staleness is measured on the monotonic clock (TickCount64), not the
        // wall clock, so a host time step cannot expire or extend a kill.
        Assert.True(WorldDeathBus.TryConsumeKill(name, out var killedAtTickMs));
        Assert.InRange(killedAtTickMs, before, Environment.TickCount64);

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
    public void MixedNormalizationForms_MatchAsOneIdentity()
    {
        // Telnet listplayers may hand back the name in a different Unicode
        // normalization form than the bot's argv-configured one (NFD vs NFC).
        // Ordinal lookup on raw forms would drop the kill event; both sides
        // fold to NFC first.
        string tag = Guid.NewGuid().ToString("N");
        string nfd = $"wdt-{tag}-Zoe\u0301";          // decomposed acute
        string nfc = nfd.Normalize(NormalizationForm.FormC); // composed
        Assert.NotEqual(nfc, nfd);

        WorldDeathBus.NotifyKilled(nfd);
        Assert.True(WorldDeathBus.TryConsumeKill(nfc, out _));
        Assert.False(WorldDeathBus.TryConsumeKill(nfd, out _)); // consumed above
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

    [Fact]
    public void StaleKill_Rejected_AndConsumed()
    {
        // Virtual clock: the two-minute TTL is otherwise unreachable from a test.
        long now = 1_000_000;
        WorldDeathBus.MonotonicMs = () => now;
        try
        {
            var name = UniqueName("stale");
            WorldDeathBus.NotifyKilled(name);
            now += WorldDeathBus.KillTtlMs;
            Assert.False(WorldDeathBus.TryConsumeKill(name, out var at));
            // Consumed either way: a rejected stale kill must not respawn on
            // the next poll either.
            Assert.Equal(1_000_000, at);
            Assert.False(WorldDeathBus.TryConsumeKill(name, out _));

            var fresh = UniqueName("fresh");
            WorldDeathBus.NotifyKilled(fresh);
            now += WorldDeathBus.KillTtlMs - 1;
            Assert.True(WorldDeathBus.TryConsumeKill(fresh, out _));
        }
        finally { WorldDeathBus.ResetForTests(); }
    }

    [Fact]
    public void ExpiredUnconsumedKill_IsEvictedByTheNextNotify()
    {
        // A kill the consumer never arrives for (the bot disconnected, or the
        // kill landed between its polls and it left) has no path back: nothing
        // but a later notify can take it out of the map.
        long now = 5_000_000;
        WorldDeathBus.MonotonicMs = () => now;
        try
        {
            var abandoned = UniqueName("abandoned");
            WorldDeathBus.NotifyKilled(abandoned);
            now += WorldDeathBus.KillTtlMs;
            var fresh = UniqueName("after-abandoned");
            WorldDeathBus.NotifyKilled(fresh);

            // Evicted, not merely rejected: a rejected-but-still-stored kill
            // reads back its stamp, an evicted one reports no stamp at all.
            Assert.False(WorldDeathBus.TryConsumeKill(abandoned, out var evictedAt));
            Assert.Equal(0, evictedAt);
            // The fresh kill next to it is untouched.
            Assert.True(WorldDeathBus.TryConsumeKill(fresh, out _));
        }
        finally { WorldDeathBus.ResetForTests(); }
    }
}
