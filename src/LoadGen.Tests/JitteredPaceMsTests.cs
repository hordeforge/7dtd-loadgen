using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Pace think-time jitter: ±20% around the requested interval, deterministic
/// for a seeded RNG. Saturation matters at the int boundary: a huge --pace-ms
/// used to wrap through an out-of-range double→int cast into int.MinValue,
/// which silently disabled pacing (negative sleep loop bound) instead of
/// clamping.
/// </summary>
public sealed class JitteredPaceMsTests
{
    [Fact]
    public void NormalPace_StaysWithinPlusMinus20Percent()
    {
        var rng = new Random(7);
        for (int i = 0; i < 1000; i++)
        {
            int jittered = ActionLoop.JitteredPaceMs(1000, rng);
            Assert.InRange(jittered, 800, 1200);
        }
    }

    [Fact]
    public void Deterministic_ForSeededRng()
    {
        Assert.Equal(
            ActionLoop.JitteredPaceMs(500, new Random(42)),
            ActionLoop.JitteredPaceMs(500, new Random(42)));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue / 2)]
    public void HugePace_Saturates_NeverWrapsNegative(int paceMs)
    {
        // At this magnitude the +20% bound itself overflows int, so the only
        // meaningful properties are positivity (the old bug went negative) and
        // saturation at int.MaxValue rather than an out-of-range cast sentinel.
        var rng = new Random(1);
        for (int i = 0; i < 100; i++)
        {
            int jittered = ActionLoop.JitteredPaceMs(paceMs, rng);
            Assert.True(jittered > 0, "jitter overflow produced a non-positive pace");
        }
    }

    [Fact]
    public void LargeButRepresentablePace_StaysWithinPlusMinus20Percent()
    {
        // 100M ms keeps p*(1.2) inside int range, so the full bound applies.
        var rng = new Random(3);
        for (int i = 0; i < 100; i++)
        {
            int jittered = ActionLoop.JitteredPaceMs(100_000_000, rng);
            Assert.InRange(jittered, 80_000_000, 120_000_000);
        }
    }

    [Fact]
    public void MaxValueInput_ClampsToIntMaxValue()
    {
        // 0.8 * int.MaxValue overflows int but fits long; the top jitter bucket
        // must saturate at int.MaxValue instead of casting out of range.
        var highJitter = new[] { 0.99, 0.999 }.Select(f =>
        {
            var rng = new MockRng(f);
            return ActionLoop.JitteredPaceMs(int.MaxValue, rng);
        });
        Assert.All(highJitter, v => Assert.Equal(int.MaxValue, v));
    }

    /// <summary>RNG stub returning a fixed value, to pin exact jitter buckets.</summary>
    sealed class MockRng(double value) : Random
    {
        public override double NextDouble() => value;
    }
}
