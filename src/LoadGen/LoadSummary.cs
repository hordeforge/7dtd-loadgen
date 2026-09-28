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

    /// <summary>Cohort summary as one entry per output line. The report is saved
    /// as a run artifact and parsed by the report lanes, so the saved copy goes
    /// through <see cref="RunReport.WriteLines"/> and its bytes stay the same
    /// whichever OS wrote the run.</summary>
    public IReadOnlyList<string> ToReportLines()
    {
        var lines = new List<string>
        {
            $"LOAD_SUMMARY total={Total} pass={Pass} fail={Fail} passRate={PassRate:P2}",
            $"LOAD_TIMING elapsedMs={ElapsedMs} p50={P50Ms} p95={P95Ms} p99={P99Ms}",
            // protocolProgress is the same count as Pass (both are r.Pass); the
            // label is kept because the LOAD_CONN line is read by operators.
            $"LOAD_CONN connected={Connected} protocolProgress={Pass}",
        };
        if (StageCounts.Count > 0)
            lines.Add("LOAD_STAGES " + string.Join(" ", StageCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        lines.AddRange(FailSamples.Take(20).Select(f => $"LOAD_FAIL_SAMPLE {f}"));
        return lines;
    }

    /// <summary>LF-terminated form of <see cref="ToReportLines"/>, for the
    /// console and the embedded event text that carries a summary inline.</summary>
    public string ToReport()
    {
        var sb = new System.Text.StringBuilder();
        foreach (string line in ToReportLines()) sb.Append(line).Append('\n');
        return sb.ToString();
    }
}
