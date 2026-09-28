using System.Collections.Concurrent;
using System.Diagnostics;

namespace SevenDTD.LoadGen;

public static class LoadRunner
{
    /// <summary>How many cohort probes keep and print their transcript.</summary>
    const int LoggedProbeCount = 8;

    /// <summary>Probe-cohort concurrency: default to a wide pool-derived cap so
    /// short-lived probes overlap, always clamped to the cohort size. Shared by
    /// the probe and self-test lanes so both scale identically.</summary>
    public static int ResolveConcurrency(int requested, int count)
    {
        if (requested <= 0)
            requested = Math.Clamp(Environment.ProcessorCount * 32, 64, 512);
        return Math.Min(requested, count);
    }

    public static LoadSummary Run(
        string host, int port, string key, int timeoutMs, int count, int concurrency,
        int rampMs, bool quiet, int idBase = 1)
    {
        concurrency = Math.Max(1, Math.Min(concurrency, count));
        var results = new System.Collections.Concurrent.ConcurrentBag<(int id, ProbeResult r)>();
        var gate = new SemaphoreSlim(concurrency, concurrency);
        var swAll = Stopwatch.StartNew();
        var tasks = new Task[count];
        for (int i = 0; i < count; i++)
        {
            int slot = i;
            tasks[i] = Task.Run(async () =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (rampMs > 0 && count > 1)
                    {
                        int delay = Program.RampDelayMs(slot, count, rampMs);
                        if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);
                    }
                    // Id from the cohort slot, never from a shared counter: a
                    // counter hands ids out in thread-wakeup order, so the same
                    // run replays under a different id per bot.
                    int id = idBase + slot;
                    bool keepLines = !quiet && id < idBase + LoggedProbeCount;
                    ProbeResult r;
                    try
                    {
                        r = LiteNetProbe.Run(host, port, key, timeoutMs, id, null, keepLines: keepLines);
                    }
                    // Isolate: one faulting probe must not take Task.WaitAll down
                    // with an AggregateException and destroy the whole cohort summary.
                    catch (Exception ex)
                    {
                        var lines = new List<string>();
                        if (keepLines) lines.Add($"[{DateTime.UtcNow:O}] [fake#{id}] EX {Program.FaultText("session", ex)}");
                        r = new ProbeResult
                        {
                            Pass = false,
                            Stages = new HashSet<string>(),
                            Connected = false,
                            Lines = lines,
                            ElapsedMs = 0,
                        };
                    }
                    results.Add((id, r));
                }
                finally { gate.Release(); }
            });
        }
        Task.WaitAll(tasks);
        swAll.Stop();
        // Id order, not completion order: the cohort transcript is a replay
        // artifact, and a bag enumerates in whatever order probes finished.
        var ordered = results.OrderBy(x => x.id).ToList();
        if (!quiet)
            foreach (var (_, r) in ordered.Take(LoggedProbeCount))
                foreach (string line in r.Lines)
                    Console.WriteLine(line);
        var list = ordered.Select(x => x.r).ToList();
        var latencies = list.Select(r => r.ElapsedMs).OrderBy(x => x).ToArray();
        long Pct(double p)
        {
            if (latencies.Length == 0) return 0;
            int idx = (int)Math.Clamp(Math.Ceiling(p * latencies.Length) - 1, 0, latencies.Length - 1);
            return latencies[idx];
        }
        var stageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int connected = 0;
        var fails = new List<string>();
        foreach (var r in list)
        {
            if (r.Connected) connected++;
            foreach (var s in r.Stages)
            {
                stageCounts.TryGetValue(s, out int c);
                stageCounts[s] = c + 1;
            }
            if (!r.Pass && fails.Count < 32)
                fails.Add($"stages=[{string.Join(",", r.Stages.OrderBy(x => x))}] disc={r.DisconnectReason ?? "none"}");
        }
        int pass = list.Count(r => r.Pass);
        return new LoadSummary
        {
            Total = list.Count,
            Pass = pass,
            Fail = list.Count - pass,
            ElapsedMs = swAll.ElapsedMilliseconds,
            P50Ms = Pct(0.50),
            P95Ms = Pct(0.95),
            P99Ms = Pct(0.99),
            Connected = connected,
            StageCounts = stageCounts,
            FailSamples = fails,
        };
    }
}
