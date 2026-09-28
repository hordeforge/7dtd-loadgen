namespace SevenDTD.LoadGen;

/// <summary>Cohort-wide client-perceived latency samples (LiteNetLib RTT).
/// "Laggy server" from the player's seat is RTT + sim stall; this captures
/// the wire half so APM can separate network lag from tick stall.</summary>
public static class PingStats
{
    /// <summary>Retained samples. Past this the store halves in place instead
    /// of dropping new arrivals, so a long soak keeps estimating percentiles
    /// over the whole run rather than freezing on the first window.</summary>
    internal const int MaxSamples = 200_000;
    const int InitialCapacity = 8192;

    static readonly object Gate = new();
    static readonly List<int> Samples = new(InitialCapacity);
    /// <summary>Every sample ever recorded, kept or not.</summary>
    static long _recorded;
    /// <summary>One in N samples is retained. Doubles on each halving.</summary>
    static int _stride = 1;

    public static void Record(int ms)
    {
        lock (Gate)
        {
            // Phase-aligned decimation: after a halving the retained set is
            // exactly the even indices of the old one, which are the multiples
            // of the new stride in the global stream, so the surviving sample
            // stays an unbiased draw from every part of the run.
            if (_recorded++ % _stride != 0) return;
            Samples.Add(ms);
            if (Samples.Count < MaxSamples) return;
            int keep = MaxSamples / 2;
            for (int i = 0; i < keep; i++)
                Samples[i] = Samples[i * 2];
            Samples.RemoveRange(keep, Samples.Count - keep);
            _stride *= 2;
        }
    }

    /// <summary>Test seam: clears accumulated samples (same pattern as
    /// GameJoinClient.ResetShutdownForTests). Only valid while no live client
    /// loop is recording.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Samples.Clear();
            _recorded = 0;
            _stride = 1;
        }
    }

    /// <summary>Test seam: samples currently held in memory.</summary>
    internal static int RetainedForTests
    {
        get { lock (Gate) return Samples.Count; }
    }

    /// <summary>count and every derived value cover the whole run: the first
    /// <c>stride</c> samples were exact, the rest are uniform over the
    /// decimation, so percentiles and the spike count are scaled estimates and
    /// <c>stride</c> says how far.</summary>
    public static (int count, double avg, int p50, int p95, int max, int spikes, int stride) Summary()
    {
        lock (Gate)
        {
            if (Samples.Count == 0) return (0, 0, 0, 0, 0, 0, 1);
            var sorted = Samples.OrderBy(x => x).ToList();
            int Pct(double p) => sorted[Math.Min(sorted.Count - 1, (int)(p * (sorted.Count - 1)))];
            return ((int)Math.Min(_recorded, int.MaxValue),
                Math.Round(sorted.Average() * _stride, 1),
                Pct(0.5), Pct(0.95), sorted[^1],
                (int)Math.Min(sorted.Count(s => s >= 150) * (long)_stride, int.MaxValue),
                _stride);
        }
    }
}
