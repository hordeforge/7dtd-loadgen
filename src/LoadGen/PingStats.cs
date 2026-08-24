namespace SevenDTD.LoadGen;

/// <summary>Cohort-wide client-perceived latency samples (LiteNetLib RTT).
/// "Laggy server" from the player's seat is RTT + sim stall; this captures
/// the wire half so APM can separate network lag from tick stall.</summary>
public static class PingStats
{
    static readonly object Gate = new();
    static readonly List<int> Samples = new(8192);

    public static void Record(int ms)
    {
        lock (Gate)
            if (Samples.Count < 200_000) Samples.Add(ms);
    }

    /// <summary>Test seam: clears accumulated samples (same pattern as
    /// GameJoinClient.ResetShutdownForTests). Only valid while no live client
    /// loop is recording.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Samples.Clear();
        }
    }

    public static (int count, double avg, int p50, int p95, int max, int spikes) Summary()
    {
        lock (Gate)
        {
            if (Samples.Count == 0) return (0, 0, 0, 0, 0, 0);
            var sorted = Samples.OrderBy(x => x).ToList();
            int Pct(double p) => sorted[Math.Min(sorted.Count - 1, (int)(p * (sorted.Count - 1)))];
            return (sorted.Count, sorted.Average(), Pct(0.5), Pct(0.95),
                sorted[^1], sorted.Count(s => s >= 150));
        }
    }
}
