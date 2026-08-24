using System.Text;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// TryDetectWorldDeath decides when a joined bot dies (health stat, entity
/// remove, or chat GMSG) and drives the respawn loop's DEATH/RESPAWN metrics.
/// The comments at the call site document two past cohort-corruption bugs this
/// suite pins against: matching the bare "refake" prefix flipped every bot on
/// one bot's death GMSG, and "refake33 died" must never kill bot refake3
/// (whole-word match only). Body layouts come from the docstring:
/// EntityStatChanged = entityId:i32, instigatorId:i32, enumStat:u8,
/// value:f32, max:f32, maxMod:f32; entity-remove packages lead with entityId:i32;
/// chat bodies are BinaryReader.ReadString strings.
/// </summary>
public sealed class WorldDeathDetectionTests
{
    static GameJoinClient Bot(int entityId, out GameJoinClient.Options opt)
    {
        opt = new GameJoinClient.Options { PlayerName = "REFake", ClientId = 3 };
        var client = new GameJoinClient { };
        client.State.EntityId = entityId;
        return client;
    }

    static byte[] StatBody(int entityId, float health)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(entityId); w.Write(entityId + 1); w.Write((byte)0); // enumStat.Health = 0
        w.Write(health); w.Write(100f); w.Write(1f);
        return ms.ToArray();
    }

    static byte[] RemoveBody(int entityId)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(entityId);
        return ms.ToArray();
    }

    static byte[] ChatBody(string s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(s);
        return ms.ToArray();
    }

    [Fact]
    public void HealthZero_ForOurEntity_IsWorldKilled()
    {
        var client = Bot(171, out var opt);
        var logs = new List<string>();
        client.TryDetectWorldDeath("NetPackageEntityStatChanged", StatBody(171, 0f), opt, logs.Add);

        Assert.True(client.State.Died);
        Assert.Equal("world_killed", client.State.DeathCause);
        Assert.Contains(logs, l => l.StartsWith("DEATH cause=world_killed"));
    }

    [Theory]
    [InlineData(999f)]       // still alive
    [InlineData(0.02f)]      // above the <= 0.01 threshold
    public void PositiveHealth_DoesNotKill(float health)
    {
        var client = Bot(171, out var opt);
        client.TryDetectWorldDeath("NetPackageEntityStatChanged", StatBody(171, health), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void OtherBotsHealthZero_DoesNotKillUs()
    {
        var client = Bot(171, out var opt);
        client.TryDetectWorldDeath("NetPackageEntityStatChanged", StatBody(172, 0f), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void TruncatedStatBody_Ignored()
    {
        var client = Bot(171, out var opt);
        client.TryDetectWorldDeath("NetPackageEntityStatChanged", new byte[20], opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void EntityRemove_OurEntity_Kills()
    {
        var client = Bot(171, out var opt);
        client.TryDetectWorldDeath("NetPackageEntityRemove", RemoveBody(171), opt, _ => { });
        Assert.True(client.State.Died);
        Assert.Equal("world_death", client.State.DeathCause);
    }

    [Fact]
    public void EntityRemove_OtherEntity_DoesNotKill()
    {
        var client = Bot(171, out var opt);
        client.TryDetectWorldDeath("NetPackageEntityDestroy", RemoveBody(172), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Theory]
    [InlineData("REFake3 died", "world_death")]
    [InlineData("REFake3 drowned", "world_drown")]
    [InlineData("REFake3 was killed by a zombie", "world_killed")]
    [InlineData("REFake3 died from radiation", "world_radiation")]
    public void OwnDeathChat_Kills_WithCause(string gmsg, string cause)
    {
        var client = Bot(3, out var opt);
        client.TryDetectWorldDeath("NetPackageGameMessage", ChatBody(gmsg), opt, _ => { });
        Assert.True(client.State.Died);
        Assert.Equal(cause, client.State.DeathCause);
    }

    [Fact]
    public void DigitSuffixName_IsNotUs()
    {
        // The documented whole-word rule: "refake33 died" must not flip bot
        // refake3 (one bot's GMSG used to kill differently-numbered bots).
        var client = Bot(3, out var opt);
        client.TryDetectWorldDeath("NetPackageSimpleChat", ChatBody("REFake33 died"), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void DeathWordsAboutSomeoneElse_DoNotKill()
    {
        // Death vocabulary alone is not enough: the old "any GMSG containing
        // 'player'" fallback killed bystander bots on unrelated chatter.
        var client = Bot(3, out var opt);
        client.TryDetectWorldDeath(
            "NetPackageGameMessage", ChatBody("zombie horde incoming, players beware"), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void OurNameWithoutDeathWords_DoesNotKill()
    {
        var client = Bot(3, out var opt);
        client.TryDetectWorldDeath("NetPackageSimpleChat", ChatBody("REFake3 says hi"), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void ShortOrEmptyChat_Ignored()
    {
        var client = Bot(3, out var opt);
        client.TryDetectWorldDeath("NetPackageSimpleChat", ChatBody(""), opt, _ => { });
        client.TryDetectWorldDeath("NetPackageSimpleChat", ChatBody("abc"), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Theory]
    [InlineData("refake3 died", "refake3", true)]
    [InlineData("refake33 died", "refake3", false)]
    [InlineData("poor refake3!", "refake3", true)]   // punctuation-bounded counts
    [InlineData("notarefake3", "refake3", false)]    // glued inside a longer word
    [InlineData("anything", "", false)]              // empty name never matches
    public void ContainsWord_MatchesWholeWordsOnly(string haystack, string word, bool want)
        => Assert.Equal(want, GameJoinClient.ContainsWord(haystack, word));
}
