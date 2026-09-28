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
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageEntityStatChanged"), StatBody(171, 0f), opt, logs.Add);

        Assert.True(client.State.Died);
        Assert.Equal(DeathCause.WorldKilled, client.State.DeathCause);
        Assert.Contains(logs, l => l.StartsWith("DEATH cause=world_killed"));
    }

    [Theory]
    [InlineData(999f)]       // still alive
    [InlineData(0.02f)]      // above the <= 0.01 threshold
    public void PositiveHealth_DoesNotKill(float health)
    {
        var client = Bot(171, out var opt);
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageEntityStatChanged"), StatBody(171, health), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void OtherBotsHealthZero_DoesNotKillUs()
    {
        var client = Bot(171, out var opt);
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageEntityStatChanged"), StatBody(172, 0f), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void TruncatedStatBody_Ignored()
    {
        var client = Bot(171, out var opt);
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageEntityStatChanged"), new byte[20], opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void EntityRemove_OurEntity_Kills()
    {
        var client = Bot(171, out var opt);
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageEntityRemove"), RemoveBody(171), opt, _ => { });
        Assert.True(client.State.Died);
        Assert.Equal(DeathCause.WorldDeath, client.State.DeathCause);
    }

    [Fact]
    public void EntityRemove_OtherEntity_DoesNotKill()
    {
        var client = Bot(171, out var opt);
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageEntityDestroy"), RemoveBody(172), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Theory]
    [InlineData("REFake3 died", DeathCause.WorldDeath)]
    [InlineData("REFake3 drowned", DeathCause.WorldDrown)]
    [InlineData("REFake3 was killed by a zombie", DeathCause.WorldKilled)]
    [InlineData("REFake3 died from radiation", DeathCause.WorldRadiation)]
    public void OwnDeathChat_Kills_WithCause(string gmsg, DeathCause cause)
    {
        var client = Bot(3, out var opt);
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageGameMessage"), ChatBody(gmsg), opt, _ => { });
        Assert.True(client.State.Died);
        Assert.Equal(cause, client.State.DeathCause);
    }

    [Fact]
    public void EveryCauseHasAStableReportName()
    {
        // The stats JSON, the deaths CSV and the log lines all render the
        // cause through this mapping, so its spellings are report schema.
        var expected = new Dictionary<DeathCause, string>
        {
            [DeathCause.None] = "none",
            [DeathCause.DrownFatal] = "drown_fatal",
            [DeathCause.Suicide] = "suicide",
            [DeathCause.KilledExternal] = "killed_external",
            [DeathCause.SuicideFallback] = "suicide_fallback",
            [DeathCause.WorldDeath] = "world_death",
            [DeathCause.WorldKilled] = "world_killed",
            [DeathCause.WorldDrown] = "world_drown",
            [DeathCause.WorldRadiation] = "world_radiation",
            [DeathCause.ServerDisconnect] = "server_disconnect",
            [DeathCause.TimeoutAlive] = "timeout_alive",
            [DeathCause.RespawnTimeout] = "respawn_timeout",
            [DeathCause.Exception] = "exception",
        };
        Assert.Equal(expected.Count, Enum.GetValues<DeathCause>().Length);
        foreach (var (cause, name) in expected)
            Assert.Equal(name, DeathCauseNames.Of(cause));
    }

    [Fact]
    public void DigitSuffixName_IsNotUs()
    {
        // The documented whole-word rule: "refake33 died" must not flip bot
        // refake3 (one bot's GMSG used to kill differently-numbered bots).
        var client = Bot(3, out var opt);
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageSimpleChat"), ChatBody("REFake33 died"), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void DeathWordsAboutSomeoneElse_DoNotKill()
    {
        // Death vocabulary alone is not enough: the old "any GMSG containing
        // 'player'" fallback killed bystander bots on unrelated chatter.
        var client = Bot(3, out var opt);
        client.TryDetectWorldDeath(
            PackageKinds.Of("NetPackageGameMessage"), ChatBody("zombie horde incoming, players beware"), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void OurNameWithoutDeathWords_DoesNotKill()
    {
        var client = Bot(3, out var opt);
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageSimpleChat"), ChatBody("REFake3 says hi"), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Fact]
    public void ShortOrEmptyChat_Ignored()
    {
        var client = Bot(3, out var opt);
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageSimpleChat"), ChatBody(""), opt, _ => { });
        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageSimpleChat"), ChatBody("abc"), opt, _ => { });
        Assert.False(client.State.Died);
    }

    [Theory]
    [InlineData(true)]   // NFD argv name, NFC server echo
    [InlineData(false)]  // NFC argv name, NFD server echo
    public void AccentedName_OtherNormalizationEcho_StillDetects(bool nfdConfig)
    {
        // "Zoe" + combining acute vs the precomposed form are byte-different
        // strings; an operator shell commonly supplies NFD while the game
        // relays NFC. Death detection must fold both sides to NFC or the bot
        // never respawns and cohort metrics corrupt.
        string nfdName = "Zoe\u0301";
        string nfcName = nfdName.Normalize(NormalizationForm.FormC);
        var opt = new GameJoinClient.Options
        {
            PlayerName = nfdConfig ? nfdName : nfcName,
            ClientId = 7,
        };
        var client = new GameJoinClient();
        client.State.EntityId = 707;
        // The in-game identity is PlayerName + ClientId, so the server echoes
        // "Zoé 7"; an echo of the bare name is a different player.
        string echo = (nfdConfig ? nfcName : nfdName) + "7 died";

        client.TryDetectWorldDeath(PackageKinds.Of("NetPackageGameMessage"), ChatBody(echo), opt, _ => { });

        Assert.True(client.State.Died);
        Assert.Equal(DeathCause.WorldDeath, client.State.DeathCause);
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
