namespace SevenDTD.LoadGen;

/// <summary>Aggregate action/death counters for stats-json. Longs because the
/// sum across up to 1000 bots on a multi-day run exceeds int.MaxValue (int
/// Sum wraps silently under unchecked arithmetic).</summary>
internal readonly record struct CohortCounters(
    long Walks, long Jumps, long Crouches, long Aims, long Turns, long Strafes,
    long Looks, long Chats, long Breaks, long Attacks, long Drowns, long Suicides,
    long Killed, int DiedClients, long TotalDeaths, long TotalRespawns, long TotalRejoins)
{
    public static CohortCounters FromState(JoinStateMachine sm) => new(
        sm.WalkActions, sm.JumpActions, sm.CrouchActions, sm.AimActions, sm.TurnActions,
        sm.StrafeActions, sm.LookActions, sm.ChatActions, sm.BreakBlockActions,
        sm.AttackActions, sm.DrownActions, sm.SuicideActions, sm.KilledActions,
        sm.Died ? 1 : 0, sm.DeathCount, sm.RespawnCount, sm.RejoinCount);

    public static CohortCounters operator +(CohortCounters a, CohortCounters b) => new(
        a.Walks + b.Walks, a.Jumps + b.Jumps, a.Crouches + b.Crouches, a.Aims + b.Aims,
        a.Turns + b.Turns, a.Strafes + b.Strafes, a.Looks + b.Looks, a.Chats + b.Chats,
        a.Breaks + b.Breaks, a.Attacks + b.Attacks, a.Drowns + b.Drowns,
        a.Suicides + b.Suicides, a.Killed + b.Killed, a.DiedClients + b.DiedClients,
        a.TotalDeaths + b.TotalDeaths, a.TotalRespawns + b.TotalRespawns,
        a.TotalRejoins + b.TotalRejoins);

    /// <summary>Cohort-wide fold of every bot's final state snapshot in one
    /// pass; every counter accumulates as long so the totals cannot wrap.</summary>
    public static CohortCounters Sum(IEnumerable<JoinStateMachine> states)
    {
        CohortCounters total = default;
        foreach (var sm in states)
            total += FromState(sm);
        return total;
    }
}
