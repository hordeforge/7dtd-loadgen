using System.Diagnostics;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// The listplayers parsers are the only place where unauthenticated telnet
/// text (anyone on the admin port, or a server that answers with garbage) is
/// scanned. The old patterns chained unbounded `.*?` gaps over the whole
/// response, so a response missing the tail fields cost O(n^2) and a
/// 16 KB body took ~9 s of a bot's pressure loop.
///
/// These pin the three contracts that matter: no exception escapes, every
/// token handed back is safe to interpolate into an admin command, and the
/// scan stays linear in the response size.
/// </summary>
public sealed class TelnetAdminFuzzTests
{
    const string LiveRow = "  1. id=171, name=bot0, health=100, pltfmid=Local_REFake171, ip=127.0.0.1, pos=";

    [Fact]
    public void LiveRows_RoundTripEveryField()
    {
        var rows = TelnetAdmin.ParseLivePlayerRows(LiveRow);

        Assert.Equal([(171, "REFake171")], rows);
    }

    [Fact]
    public void LiveRows_ManyRows_AllParsedInOrder()
    {
        var sb = new System.Text.StringBuilder("INFO: Players online:\n");
        for (int i = 1; i <= 50; i++)
            sb.Append($"  {i}. id={i}, name=bot{i}, health=100, "
                    + $"pltfmid=Local_REFake{i}, ip=127.0.0.1, pos=1,2,3\n");

        var rows = TelnetAdmin.ParseLivePlayerRows(sb.ToString());

        Assert.Equal(50, rows.Count);
        Assert.Equal((1, "REFake1"), rows[0]);
        Assert.Equal((50, "REFake50"), rows[^1]);
        Assert.Equal(Enumerable.Range(1, 50).ToList(),
                     TelnetAdmin.ParseLivingPlayerIds(sb.ToString()));
    }

    [Fact]
    public void ZeroHealth_And_NonLoopback_AreNotPlayers()
    {
        // Leftovers from a prior kill (health=0) and a non-loopback row are not
        // live players; neither may become a kill/spawn target.
        string outp = "id=1, health=0, pltfmid=Local_REFake1, ip=127.0.0.1, "
                    + "id=2, health=50, pltfmid=Local_REFake2, ip=10.0.0.5, "
                    + "id=3, health=50, pltfmid=Local_REFake3, ip=127.0.0.1, ";

        Assert.Equal([(3, "REFake3")], TelnetAdmin.ParseLivePlayerRows(outp));
        // The horde picker keys on id + health only, so a non-loopback row is
        // still a living player there; only the token-bearing parsers need ip.
        Assert.Equal([2, 3], TelnetAdmin.ParseLivingPlayerIds(outp));
    }

    [Fact]
    public void OverflowingId_DoesNotWrapIntoAKillableId()
    {
        // A crafted id wider than Int32 must be dropped, not truncated to a
        // small id that then becomes an admin command target.
        string outp = "id=99999999999999999999, health=100, pltfmid=Local_REFake7, ip=127.0.0.1, ";

        Assert.Empty(TelnetAdmin.ParseLivePlayerRows(outp));
        Assert.Empty(TelnetAdmin.ParseLivingPlayerIds(outp));
    }

    [Fact]
    public void HostileResponse_ParsesInLinearTime()
    {
        // Anchors everywhere, tail fields nowhere: the shape the old `.*?`
        // chain blew up on. 256 KB of it must not cost more than a few scans.
        const string chunk = "id=1, health=5 filler ";
        string hostile = string.Concat(
            Enumerable.Repeat(chunk, (256 * 1024) / chunk.Length + 1))[..(256 * 1024)];

        var sw = Stopwatch.StartNew();
        var live = TelnetAdmin.ParseLivePlayerRows(hostile);
        var fallback = TelnetAdmin.ParseFallbackPlayerRows(hostile);
        var ids = TelnetAdmin.ParseLivingPlayerIds(hostile);
        sw.Stop();

        Assert.Empty(live);
        Assert.Empty(fallback);
        Assert.NotEmpty(ids);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                    $"row scan took {sw.ElapsedMilliseconds} ms on {hostile.Length} bytes");
    }

    [Fact]
    public void CraftedTokens_NeverReachTheCommandBuilder()
    {
        // A server-supplied name that carries a command separator must be
        // dropped, not escaped: the parser has no sanitizer, only a filter.
        foreach (string bad in new[] { "REFake1,x", "REFake1 x", "REFake1\nkill", "REFake1'ip=1.2.3.4", "" })
        {
            string outp = $"id=7, health=100, pltfmid=Local_{bad}, ip=127.0.0.1, ";
            var rows = TelnetAdmin.ParseLivePlayerRows(outp);
            Assert.All(rows, r => Assert.True(TelnetAdmin.IsSafeCommandToken(r.token)));
        }
    }

    [Fact]
    public void SeedCorpusFuzz_OnlyContractedResultsEscape()
    {
        // Seeds are the shapes the console actually prints; the generator keeps
        // a well-formed prefix and mutates the tail, so the fuzzer explores
        // around real layouts instead of pure noise.
        string[] seeds =
        {
            LiveRow,
            "  2. id=172, name=bot1, health=0, pltfmid=Local_REFake172, ip=127.0.0.1, pos=",
            "id=3 health=10 pltfmid=Local_REFake3 ip=127.0.0.1",
            "ID = 4 , HEALTH = 10 , PLTFMID = local_refake4 , IP = 127.0.0.1",
            "Total of 3 in the game",
            "id=",
            "id=-1, health=-1, pltfmid=Local_, ip=",
            "",
        };
        var rng = new Random(0x7E1E7);
        var charPool = "idhealthpltfmidLocal_REFake127.=, \t\r\n\x00\u00e9".ToCharArray();

        for (int iter = 0; iter < 20_000; iter++)
        {
            string src = seeds[rng.Next(seeds.Length)];
            int len = rng.Next(0, src.Length + 24);
            char[] buf = src[..Math.Min(src.Length, len)].ToCharArray();
            Array.Resize(ref buf, len);
            for (int i = 0; i < buf.Length; i++)
            {
                int roll = rng.Next(10);
                if (roll < 2) buf[i] = charPool[rng.Next(charPool.Length)];
                else if (roll == 2) buf[i] = ' ';
                else if (roll == 3) buf[i] = (char)rng.Next(char.MaxValue);
            }
            string data = new string(buf);

            List<(int id, string token)> live = new();
            List<(int id, string token)> fallback = new();
            List<int> ids = new();
            var ex = Record.Exception(() =>
            {
                live = TelnetAdmin.ParseLivePlayerRows(data);
                fallback = TelnetAdmin.ParseFallbackPlayerRows(data);
                ids = TelnetAdmin.ParseLivingPlayerIds(data);
            });
            if (ex != null)
                Assert.Fail($"{ex.GetType().Name}: {ex.Message} input={Describe(data)}");

            Assert.All(live, r =>
            {
                Assert.True(r.id > 0, $"non-positive id from {Describe(data)}");
                Assert.True(TelnetAdmin.IsSafeCommandToken(r.token), $"unsafe token {r.token}");
            });
            Assert.All(fallback, r => Assert.True(r.id > 0));
            Assert.All(ids, id => Assert.True(id > 0));
        }
    }

    static string Describe(string s) =>
        s.Length <= 120 ? $"\"{s}\"" : $"\"{s[..120]}\"... (len={s.Length})";
}
