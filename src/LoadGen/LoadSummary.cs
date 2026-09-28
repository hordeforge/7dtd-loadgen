namespace SevenDTD.LoadGen;

public sealed class LoadSummary
{
    public int Total { get; init; }
    public int Pass { get; init; }
    public int Fail { get; init; }
    public double PassRate => Total == 0 ? 0 : (double)Pass / Total;
    public long ElapsedMs { get; init; }
    public long P50Ms { get; init; }
    public long P95Ms { get; init; }
    public long P99Ms { get; init; }
    public int Connected { get; init; }
    public Dictionary<string, int> StageCounts { get; init; } = new();
    public List<string> FailSamples { get; init; } = new();

    /// <summary>Cohort summary, LF-terminated: the report is saved as a run
    /// artifact and parsed by the report lanes, so its bytes stay the same
    /// whichever OS wrote the run.</summary>
    public string ToReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"LOAD_SUMMARY total={Total} pass={Pass} fail={Fail} passRate={PassRate:P2}\n");
        sb.Append($"LOAD_TIMING elapsedMs={ElapsedMs} p50={P50Ms} p95={P95Ms} p99={P99Ms}\n");
        // protocolProgress is the same count as Pass (both are r.Pass); the
        // label is kept because the LOAD_CONN line is read by operators.
        sb.Append($"LOAD_CONN connected={Connected} protocolProgress={Pass}\n");
        if (StageCounts.Count > 0)
            sb.Append("LOAD_STAGES " + string.Join(" ", StageCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")) + "\n");
        foreach (var f in FailSamples.Take(20))
            sb.Append($"LOAD_FAIL_SAMPLE {f}\n");
        return sb.ToString();
    }
}
