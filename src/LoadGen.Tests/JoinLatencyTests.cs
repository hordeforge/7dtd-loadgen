using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// JoinLatency.Summary is the join-path latency the stats json and the
/// JOIN_LATENCY summary report, so the two rules that make it meaningful must
/// hold: a bot that never joined contributes no sample, and percentiles index
/// the sorted joined set.
/// </summary>
public sealed class JoinLatencyTests
{
    [Fact]
    public void NoSamples_ReportsZeros()
    {
        Assert.Equal((0, 0, 0, 0), JoinLatency.Summary(Array.Empty<int>()));
    }

    [Fact]
    public void FailedJoins_AreExcluded()
    {
        // -1 is JoinStateMachine's "never joined" marker; folding it in would
        // report a cohort that never joined as 0 ms joins.
        var (count, p50, p95, max) = JoinLatency.Summary(new[] { -1, 120, -1, 60, -1 });
        Assert.Equal(2, count);
        Assert.Equal(60, p50);
        // sorted=[60,120]: p95 -> idx (int)(0.95*1)=0 => 60, max=120.
        Assert.Equal(60, p95);
        Assert.Equal(120, max);
    }

    [Fact]
    public void AllFailedJoins_ReportsNoSamples()
    {
        Assert.Equal((0, 0, 0, 0), JoinLatency.Summary(new[] { -1, -1 }));
    }

    [Fact]
    public void Percentiles_IndexIntoSortedSet()
    {
        // sorted=[100,200,300,400]: p50 -> idx 1 => 200, p95 -> idx 2 => 300.
        var (count, p50, p95, max) = JoinLatency.Summary(new[] { 400, 100, 300, 200 });
        Assert.Equal(4, count);
        Assert.Equal(200, p50);
        Assert.Equal(300, p95);
        Assert.Equal(400, max);
    }
}
