using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// AGENTS.md critical rule 6: fake clients bind unique 127.a.b.c addresses so
/// the dedicated server's per-IP connect throttle cannot collapse a cohort
/// into one bucket (a regression here silently skews every load run without
/// failing a join). Pins: first bot on 127.0.0.1, loopback prefix always,
/// injectivity across realistic cohort+attempt strides, determinism.
/// </summary>
public sealed class LoopbackBindForIndexTests
{
    const int AttemptStride = 17; // Program.cs binds clientId + attempt * 17

    [Fact]
    public void FirstBot_BindsLoopbackOne()
    {
        Assert.Equal("127.0.0.1", GameJoinClient.LoopbackBindForIndex(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    public void Binds_AreDeterministic(int index)
    {
        Assert.Equal(GameJoinClient.LoopbackBindForIndex(index), GameJoinClient.LoopbackBindForIndex(index));
    }

    [Fact]
    public void CohortPlusRejoinAttempts_AllUnique_AndLoopback()
    {
        // A 64-bot cohort over 30 rejoin attempts spans indices up to
        // 63 + 29*17 = 556; sweep far past that to cover future scales.
        var seen = new HashSet<string>();
        for (int i = 0; i < 20_000; i++)
        {
            string bind = GameJoinClient.LoopbackBindForIndex(i);
            Assert.StartsWith("127.", bind);
            var octets = bind.Split('.');
            Assert.Equal(4, octets.Length);
            Assert.All(octets, o => Assert.InRange(int.Parse(o), 0, 255));
            Assert.True(seen.Add(bind), $"index {i} reuses bind {bind}");
        }
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
