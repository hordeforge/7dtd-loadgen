using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Probe/self-test cohort concurrency (--concurrency). The resolved value is
/// handed straight to SemaphoreSlim in LoadRunner.Run, so it must stay in
/// [1, count]: above the cohort size it opens gates no bot can enter, and at
/// or below zero the semaphore would never be released. The unspecified
/// default is a pool-derived cap, shared by the probe and self-test lanes so
/// both scale identically.
/// </summary>
public sealed class LoadRunnerConcurrencyTests
{
    [Theory]
    [InlineData(4, 24, 4)]
    [InlineData(1, 1, 1)]
    [InlineData(8, 8, 8)]
    public void ExplicitRequest_IsHonored(int requested, int count, int expected)
        => Assert.Equal(expected, LoadRunner.ResolveConcurrency(requested, count));

    [Theory]
    [InlineData(64, 8, 8)]
    [InlineData(512, 100, 100)]
    [InlineData(int.MaxValue, 6, 6)]
    public void RequestAboveCohort_ClampsToCount(int requested, int count, int expected)
        => Assert.Equal(expected, LoadRunner.ResolveConcurrency(requested, count));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void UnspecifiedRequest_UsesThePoolDerivedDefault(int requested)
    {
        int wide = LoadRunner.ResolveConcurrency(requested, 100_000);
        Assert.InRange(wide, 64, 512);

        // The default is a cap, not a target: a cohort smaller than the floor
        // still runs at its own size, otherwise --count 8 would open 64 gates.
        Assert.Equal(8, LoadRunner.ResolveConcurrency(requested, 8));
    }

    [Fact]
    public void EmptyCohort_ResolvesToZero()
        // Degenerate but reachable through the CLI: the count gate rejects 0
        // before this runs, and Run() clamps again, so the helper itself must
        // not invent a cohort size.
        => Assert.Equal(0, LoadRunner.ResolveConcurrency(4, 0));
}
