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
            Assert.Equal((0, 0.0, 0, 0, 0, 0, 1), PingStats.Summary());
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
            var (count, avg, p50, p95, max, spikes, stride) = PingStats.Summary();
            Assert.Equal(4, count);
            Assert.Equal(25.0, avg);
            Assert.Equal(20, p50);
            Assert.Equal(30, p95);
            Assert.Equal(40, max);
            Assert.Equal(0, spikes);
            Assert.Equal(1, stride);
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
    public void PastTheCap_TheStoreHalvesAndKeepsCoveringTheRun()
    {
        PingStats.ResetForTests();
        try
        {
            for (int i = 0; i < 200_000; i++)
                PingStats.Record(1);
            for (int i = 0; i < 500; i++)
                PingStats.Record(9000);

            var summary = PingStats.Summary();
            Assert.Equal(200_500, summary.count);
            Assert.Equal(2, summary.stride);
            Assert.True(PingStats.RetainedForTests <= PingStats.MaxSamples);
            // The tail is what a soak's regression looks like. Dropping arrivals
            // at the cap would pin every statistic to the first window.
            Assert.Equal(9000, summary.max);
        }
        finally { PingStats.ResetForTests(); }
    }

    [Fact]
    public void Decimation_LeavesTheMeanUnscaled()
    {
        PingStats.ResetForTests();
        try
        {
            // 200k samples of 1 ms, then a tail of 100 ms. The tail average
            // over the whole run is (200000*1 + 500*100) / 200500 = 1.24 ms.
            // The retained set is every Nth sample of the same stream, so its
            // mean already estimates that; scaling it by the stride reported
            // 2.5 ms and would double again at every later halving.
            for (int i = 0; i < 200_000; i++)
                PingStats.Record(1);
            for (int i = 0; i < 500; i++)
                PingStats.Record(100);

            var summary = PingStats.Summary();
            Assert.Equal(2, summary.stride);
            Assert.Equal(1.2, summary.avg, 1);
        }
        finally { PingStats.ResetForTests(); }
    }

    [Fact]
    public void Decimation_ScalesSpikeCountToTheRun()
    {
        PingStats.ResetForTests();
        try
        {
            for (int i = 0; i < 200_000; i++)
                PingStats.Record(1);
            for (int i = 0; i < 500; i++)
                PingStats.Record(5000);

            var summary = PingStats.Summary();
            // 500 spikes recorded, 250 retained, stride 2.
            Assert.Equal(500, summary.spikes);
        }
        finally { PingStats.ResetForTests(); }
    }
}
