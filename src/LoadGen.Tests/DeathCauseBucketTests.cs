using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// The DEATH_STATS buckets and their stats-json twins are the only place a run
/// says how its bots died. They only add up to the cohort total while every
/// DeathCause maps to exactly one bucket, and the table used to be open-coded
/// at the call site: a cause added to the enum without a matching arm vanished
/// from both renderings and left the counts short of the total with nothing to
/// show for it. These pin the partition.
/// </summary>
public sealed class DeathCauseBucketTests
{
    [Fact]
    public void EveryDeathCauseHasABucket()
    {
        foreach (DeathCause cause in Enum.GetValues<DeathCause>())
            Assert.False(string.IsNullOrEmpty(DeathCauseBuckets.KeyOf(cause)),
                $"{cause} has no report bucket");
    }

    [Fact]
    public void BucketKeysAreUniqueAndAllUsed()
    {
        Assert.Equal(DeathCauseBuckets.Keys.Length,
                     DeathCauseBuckets.Keys.Distinct(StringComparer.Ordinal).Count());
        var used = Enum.GetValues<DeathCause>()
            .Select(DeathCauseBuckets.KeyOf).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(DeathCauseBuckets.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     used.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>The reported property: a cohort with one bot per cause produces
    /// counts that sum to the cohort, not a short total with a silent
    /// residual.</summary>
    [Fact]
    public void BucketsPartitionTheCohort()
    {
        var cohort = Enum.GetValues<DeathCause>().ToList();
        var counts = DeathCauseBuckets.Keys
            .Select(k => (Key: k, Count: 0)).ToArray();
        foreach (DeathCause cause in cohort)
            counts[Array.IndexOf(DeathCauseBuckets.Keys,
                                 DeathCauseBuckets.KeyOf(cause))].Count++;

        Assert.Equal(cohort.Count, counts.Sum(c => c.Count));
        // Every cause contributes at least one bot, so no bucket is dead weight
        // in the report line.
        Assert.All(counts, c => Assert.True(c.Count > 0, $"{c.Key} is never emitted"));
    }

    /// <summary>A bot that walked its actions and exited cleanly carries
    /// <see cref="DeathCause.None"/>; it is the cause most likely to go
    /// unmapped, and the one whose loss hides a normal run.</summary>
    [Fact]
    public void NoneIsBucketed()
    {
        Assert.Equal("none", DeathCauseBuckets.KeyOf(DeathCause.None));
    }
}
