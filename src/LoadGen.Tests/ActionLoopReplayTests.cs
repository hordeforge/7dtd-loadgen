using System.Numerics;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Replay of a whole action loop from one seed. The loop takes its clock and
/// its think-time wait from <see cref="ActionLoop.Options"/>, so a run can be
/// driven on virtual time and compared byte-for-byte across executions: the log
/// stream, the counters and the final position must match, and the lifetime
/// cutoff must land on the same step. A leak in either the seeded RNG or the
/// pace stream shows up here as a diff between the two runs.
/// </summary>
public sealed class ActionLoopReplayTests
{
    const int RunSeed = 4242;
    const int Actions = 200;
    const int PaceMs = 40;

    // NetPackagePackageIds sits at index 0 in the real mapping; the rest are
    // the packages the action loop can address, in id order.
    static readonly string[] Mappings =
    {
        "NetPackagePackageIds",
        "NetPackageEntityPosAndRot",
        "NetPackageEntityRelPosAndRot",
        "NetPackageEntityAliveFlags",
        "NetPackageDamageEntity",
        "NetPackageSimpleChat",
        "NetPackageExplosionInitiate",
    };

    sealed record Replay(List<string> Log, JoinStateMachine State, List<byte[]> Sent, long SleptMs);

    static Replay ReplayOnce(int seed, int maxLifetimeMs)
    {
        var sm = new JoinStateMachine();
        sm.ApplyPackageMappings(Mappings);
        sm.EntityId = 7;
        sm.MarkJoined();
        var lines = new List<string>();
        var sent = new List<byte[]>();
        long virtualNow = 0;
        ActionLoop.Run(sm, pkt => { sent.Add(pkt); return true; }, new ActionLoop.Options
        {
            ActionCount = Actions,
            Seed = seed,
            Mode = ActionLoop.BotMode.Mixed,
            PaceMs = PaceMs,
            MaxLifetimeMs = maxLifetimeMs,
            Log = lines.Add,
            ElapsedMs = () => virtualNow,
            Sleep = ms => virtualNow += ms,
        });
        return new Replay(lines, sm, sent, virtualNow);
    }

    [Fact]
    public void SameSeed_ReplaysIdenticalLogAndState()
    {
        Replay a = ReplayOnce(RunSeed, maxLifetimeMs: 0);
        Replay b = ReplayOnce(RunSeed, maxLifetimeMs: 0);

        Assert.Equal(a.Log, b.Log);
        Assert.Equal(a.Sent, b.Sent);
        Assert.Equal(a.State.PosX, b.State.PosX);
        Assert.Equal(a.State.PosY, b.State.PosY);
        Assert.Equal(a.State.PosZ, b.State.PosZ);
        Assert.Equal(a.State.WalkActions, b.State.WalkActions);
        Assert.Equal(a.State.TurnActions, b.State.TurnActions);
        Assert.Equal(a.State.ChatActions, b.State.ChatActions);
    }

    [Fact]
    public void SameSeed_ReportsTheVirtualElapsedTime()
    {
        // The summary carries elapsedMs; on virtual time it is a function of
        // the injected clock, not of how fast the host ran the loop. Every
        // think-time wait the loop asked for is charged to that clock, so the
        // reported total is exactly the sum of the injected sleeps.
        Replay a = ReplayOnce(RunSeed, maxLifetimeMs: 0);
        string summary = a.Log[^1];
        Assert.StartsWith("ACTION_SUMMARY ", summary);
        long elapsed = long.Parse(
            summary[(summary.IndexOf("elapsedMs=", StringComparison.Ordinal) + 10)..]
                .Split(' ')[0]);
        Assert.Equal(a.SleptMs, elapsed);
        Assert.InRange(elapsed, Actions * PaceMs * 8 / 10, Actions * PaceMs * 12 / 10);
    }

    [Fact]
    public void VirtualClock_CutsTheLifetimeAtTheSameStepOnEveryReplay()
    {
        const int LifetimeMs = Actions * PaceMs / 2;
        Replay a = ReplayOnce(RunSeed, LifetimeMs);
        Replay b = ReplayOnce(RunSeed, LifetimeMs);

        Assert.Equal(a.Log, b.Log);
        Assert.Contains("cause=timeout_alive", a.Log[^1]);
        Assert.Equal(DeathCause.TimeoutAlive, a.State.DeathCause);
        // Half the budget: the cutoff fired, and it fired well before the run
        // would have ended on its own.
        Assert.True(a.State.WalkActions < Actions,
            $"expected the lifetime cutoff to end the run early, walks={a.State.WalkActions}");
    }

    [Fact]
    public void DifferentSeed_TakesADifferentPath()
    {
        Replay a = ReplayOnce(RunSeed, maxLifetimeMs: 0);
        Replay c = ReplayOnce(RunSeed + 1, maxLifetimeMs: 0);
        Assert.NotEqual(a.Log, c.Log);
    }

    [Fact]
    public void NeighbouringBotSeeds_AreNotOnASharedLattice()
    {
        // Bot seeds are actionSeed + clientId + life * 997, so consecutive
        // clients hand Random neighbouring seeds. A seeded Random lays opening
        // values for nearby seeds on a fixed lattice - the value for client id n+2
        // sits a near-constant step away from the one for n - which starts a
        // cohort in near-lockstep. After mixing, that step must vary.
        var opening = Enumerable.Range(0, 16)
            .Select(id => new Random(BotRng.Decorrelate(RunSeed + id)).NextDouble())
            .ToList();
        var steps = Enumerable.Range(0, opening.Count - 2)
            .Select(i => opening[i + 2] - opening[i])
            .ToList();
        double spread = steps.Max() - steps.Min();
        Assert.True(spread > 0.1, $"opening values still step by a near-constant {spread:F6}");
    }

    [Fact]
    public void MixedSeed_AvalanchesAcrossNeighbouringInputs()
    {
        // One flipped input bit must move about half the output bits, or the
        // seeds of two clients whose ids differ by one stay neighbours after
        // mixing. Measured over 256 neighbouring pairs the mixed seeds move
        // 16.25 bits on average; 15 is the floor that still catches a mixer
        // that only shifts the high bits.
        int moved = 0;
        for (int seed = 0; seed < 256; seed++)
            moved += BitOperations.PopCount(
                (uint)(BotRng.Decorrelate(seed) ^ BotRng.Decorrelate(seed + 1)));
        Assert.True(moved / 256.0 >= 15, $"neighbouring seeds moved {moved / 256.0:F2} bits on average");
    }

    [Fact]
    public void MixedSeed_IsStable()
    {
        // The seed stays a sufficient key: a rerun of the same bot seed must
        // draw the same stream, or a recorded seed cannot replay a run.
        Assert.Equal(BotRng.Decorrelate(RunSeed), BotRng.Decorrelate(RunSeed));
    }
}
