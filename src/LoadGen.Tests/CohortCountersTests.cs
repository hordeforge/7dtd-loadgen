using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Cohort aggregation contract: the multi-bot summary folds every bot's final
/// state snapshot through CohortCounters.Sum, so the totals must match a manual
/// per-field sum, count died clients exactly once each, and accumulate as long.
/// </summary>
public sealed class CohortCountersTests
{
    static JoinStateMachine Bot(int walks, int deaths, int respawns, int rejoins, bool died)
    {
        var sm = new JoinStateMachine
        {
            WalkActions = walks,
            DeathCount = deaths,
            RespawnCount = respawns,
            RejoinCount = rejoins,
            Died = died,
        };
        return sm;
    }

    [Fact]
    public void Sum_FoldsEveryCounter_AcrossBots()
    {
        var cohort = CohortCounters.Sum(new[]
        {
            Bot(10, 2, 1, 0, died: true),
            Bot(5, 0, 0, 3, died: false),
            Bot(0, 1, 1, 0, died: true),
        });

        Assert.Equal(15, cohort.Walks);
        Assert.Equal(3, cohort.TotalDeaths);
        Assert.Equal(2, cohort.TotalRespawns);
        Assert.Equal(3, cohort.TotalRejoins);
        // DiedClients counts bots whose final snapshot says died, one each.
        Assert.Equal(2, cohort.DiedClients);
    }

    [Fact]
    public void Sum_FoldsEveryActionCounter()
    {
        // One field set, three manual enumerations (record, FromState, +).
        // A counter added to the state but not to the fold is invisible in
        // every artifact, so pin the whole action set at once.
        var a = new JoinStateMachine
        {
            WalkActions = 1,
            JumpActions = 2,
            CrouchActions = 3,
            AimActions = 4,
            TurnActions = 5,
            StrafeActions = 6,
            LookActions = 7,
            ChatActions = 8,
            BreakBlockActions = 9,
            DynamiteActions = 10,
            AttackActions = 11,
            DrownActions = 12,
            SuicideActions = 13,
            KilledActions = 14,
        };
        var cohort = CohortCounters.Sum([a, a]);
        Assert.Equal(
            new long[] { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28 },
            new[]
            {
                cohort.Walks, cohort.Jumps, cohort.Crouches, cohort.Aims, cohort.Turns,
                cohort.Strafes, cohort.Looks, cohort.Chats, cohort.Breaks, cohort.Dynamite,
                cohort.Attacks, cohort.Drowns, cohort.Suicides, cohort.Killed,
            });
    }

    [Fact]
    public void Sum_EmptyCohort_IsZero()
    {
        var cohort = CohortCounters.Sum([]);
        Assert.Equal(0, cohort.Walks);
        Assert.Equal(0, cohort.DiedClients);
        Assert.Equal(0, cohort.TotalDeaths);
    }

    [Fact]
    public void Sum_FromStateMatchesPerBotSnapshot()
    {
        var sm = Bot(7, 1, 1, 2, died: true);
        var single = CohortCounters.Sum([sm]);
        var direct = CohortCounters.FromState(sm);
        Assert.Equal(direct, single);
    }
}
