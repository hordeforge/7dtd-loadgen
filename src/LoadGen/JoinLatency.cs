namespace SevenDTD.LoadGen;

/// <summary>Cohort join-handshake latency over the per-bot
/// <see cref="JoinStateMachine.JoinMs"/> values. Bots that never joined are
/// absent, not zero: a failed join has no duration and folding it in would
/// drag every percentile down by however long the attempt was ignored.</summary>
public static class JoinLatency
{
    /// <summary>(samples, p50, p95, max) in milliseconds over the joined bots.
    /// A run with no successful join reports zeros, keeping the stats json one
    /// numeric shape for the report lanes.</summary>
    public static (int count, int p50, int p95, int max) Summary(IEnumerable<int> samplesMs)
    {
        var joined = samplesMs.Where(ms => ms >= 0).OrderBy(ms => ms).ToList();
        if (joined.Count == 0) return (0, 0, 0, 0);
        int Pct(double p) => joined[Math.Min(joined.Count - 1, (int)(p * (joined.Count - 1)))];
        return (joined.Count, Pct(0.5), Pct(0.95), joined[^1]);
    }
}
