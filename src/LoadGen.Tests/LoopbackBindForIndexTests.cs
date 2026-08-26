using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// AGENTS.md critical rule 6: fake clients bind unique 127.a.b.c addresses so
/// the dedicated server's per-IP connect throttle cannot collapse a cohort
/// into one bucket (a regression here silently skews every load run without
/// failing a join). Pins: the first bot of a cohort on 127.0.0.1, loopback
/// prefix always, injectivity of the (clientId, attempt) -> address map across
/// realistic cohort+attempt grids, determinism.
/// </summary>
public sealed class LoopbackBindForIndexTests
{
    [Fact]
    public void FirstBot_BindsLoopbackOne()
    {
        Assert.Equal("127.0.0.1", GameJoinClient.LoopbackBindForIndex(0));
        Assert.Equal("127.0.0.1", GameJoinClient.LoopbackBindFor(1, 1));
    }

    [Fact]
    public void ClientAttemptGrid_AllUnique_AndLoopback()
    {
        // A 64-bot cohort over 30 rejoin attempts must never hand two live bots
        // the same bind: the old inline map (clientId + attempt * 17) collided
        // whenever two clients 17 apart were one attempt apart (e.g. client 52
        // attempt 1 vs client 35 attempt 2). Sweep past every documented scale.
        var seen = new HashSet<string>();
        for (int client = 1; client <= 256; client++)
            for (int attempt = 1; attempt <= 30; attempt++)
            {
                string bind = GameJoinClient.LoopbackBindFor(client, attempt);
                Assert.StartsWith("127.", bind);
                var octets = bind.Split('.');
                Assert.Equal(4, octets.Length);
                Assert.All(octets, o => Assert.InRange(int.Parse(o), 0, 255));
                Assert.True(seen.Add(bind),
                    $"client {client} attempt {attempt} reuses bind {bind}");
            }
    }

    [Fact]
    public void AttemptStride_ExceedsCohortSpan_SoMapStaysInjective()
    {
        // Injectivity argument pinned structurally: the stride must be larger
        // than any cohort the tool documents (README scaling tops out at 1000).
        Assert.True(GameJoinClient.RejoinIndexStride > 1000,
            "rejoin stride must exceed the maximum documented cohort size");
    }

    [Fact]
    public void WrapAround_RepeatsAfterFullCycle()
    {
        // The address space is one 256*256*254 period; index N and N + cycle
        // must collide by design (not alias mid-cohort) and stay well-formed.
        int cycle = 256 * 256 * 254;
        string a = GameJoinClient.LoopbackBindForIndex(1234);
        string b = GameJoinClient.LoopbackBindForIndex(1234 + cycle);
        Assert.Equal(a, b);
    }
}
