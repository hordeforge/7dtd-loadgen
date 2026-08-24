using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// PingStats.Summary is the wire-latency half of the APM lag diagnosis
/// ("separate network lag from tick stall"), so the percentile indexing,
/// spike threshold, and sample cap must hold exactly. The store is
/// process-global and MockGameServerConcurrentPollTests records real RTT
/// samples into it, so this class shares the non-parallel collection with
/// that test, resets at start, and restores a clean store in finally.
/// </summary>
[Collection("process-shutdown-sweep")]
public sealed class PingStatsTests
{
    [Fact]
    public void Empty_ReturnsZeros()
    {
        PingStats.ResetForTests();
        try
        {
            Assert.Equal((0, 0.0, 0, 0, 0, 0), PingStats.Summary());
        }
        finally { PingStats.ResetForTests(); }
    }

    [Fact]
    public void Percentiles_IndexIntoSortedList()
    {
        PingStats.ResetForTests();
        try
        {
            foreach (var ms in new[] { 40, 10, 30, 20 })
                PingStats.Record(ms);

            // sorted=[10,20,30,40]: P50 -> idx (int)(0.5*3)=1 => 20,
            // P95 -> idx (int)(0.95*3)=2 => 30, max=40.
            var (count, avg, p50, p95, max, spikes) = PingStats.Summary();
            Assert.Equal(4, count);
            Assert.Equal(25.0, avg);
            Assert.Equal(20, p50);
            Assert.Equal(30, p95);
            Assert.Equal(40, max);
            Assert.Equal(0, spikes);
        }
        finally { PingStats.ResetForTests(); }
    }

    [Fact]
    public void SpikeThreshold_IsInclusiveAt150()
    {
        PingStats.ResetForTests();
        try
        {
            PingStats.Record(149);
            PingStats.Record(150);
            PingStats.Record(5000);
            Assert.Equal(2, PingStats.Summary().spikes);
        }
        finally { PingStats.ResetForTests(); }
    }

    [Fact]
    public void SampleCap_StopsAt200k()
    {
        PingStats.ResetForTests();
        try
        {
            for (int i = 0; i < 200_500; i++)
                PingStats.Record(i);
            Assert.Equal(200_000, PingStats.Summary().count);
        }
        finally { PingStats.ResetForTests(); }
    }
}
