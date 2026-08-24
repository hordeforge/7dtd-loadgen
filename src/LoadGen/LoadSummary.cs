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
    public int ProtocolProgress { get; init; }
    public Dictionary<string, int> StageCounts { get; init; } = new();
    public List<string> FailSamples { get; init; } = new();

    public string ToReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"LOAD_SUMMARY total={Total} pass={Pass} fail={Fail} passRate={PassRate:P2}");
        sb.AppendLine($"LOAD_TIMING elapsedMs={ElapsedMs} p50={P50Ms} p95={P95Ms} p99={P99Ms}");
        sb.AppendLine($"LOAD_CONN connected={Connected} protocolProgress={ProtocolProgress}");
        if (StageCounts.Count > 0)
            sb.AppendLine("LOAD_STAGES " + string.Join(" ", StageCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var f in FailSamples.Take(20))
            sb.AppendLine($"LOAD_FAIL_SAMPLE {f}");
        return sb.ToString();
    }
}
