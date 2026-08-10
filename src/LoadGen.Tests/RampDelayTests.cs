using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Ramp stagger math for --ramp-ms (join pacing). Linear across the cohort,
/// first bot at 0, clamp protects the Task.Delay(int) cast at scale. The ramp
/// is the validated workaround for the stock LiteNetLib join-churn race
/// (7dtd-research docs/network.md §4.0): 24 bots at 3000 ms -> 0 drops vs 302
/// non-ramped.
/// </summary>
public sealed class RampDelayTests
{
    [Fact]
    public void FirstBot_AlwaysZero()
    {
        Assert.Equal(0, Program.RampDelayMs(0, 24, 3000));
        Assert.Equal(0, Program.RampDelayMs(0, 2, 3000));
    }

    [Fact]
    public void LastBot_GetsFullRamp()
    {
        Assert.Equal(3000, Program.RampDelayMs(23, 24, 3000));
        Assert.Equal(5000, Program.RampDelayMs(4, 5, 5000));
    }

    [Fact]
    public void Linear_IntermediateBots()
    {
        // 24 bots at 3000 ms: bot 1 is 1/23 of the ramp (~130 ms), bot 12 is 12/23.
        Assert.Equal(130, Program.RampDelayMs(1, 24, 3000));   // 3000/23 = 130
        Assert.Equal(1565, Program.RampDelayMs(12, 24, 3000)); // 36000/23 = 1565
    }

    [Fact]
    public void Disabled_Or_Single_ReturnsZero()
    {
        Assert.Equal(0, Program.RampDelayMs(5, 10, 0));    // ramp disabled
        Assert.Equal(0, Program.RampDelayMs(0, 1, 3000));  // single bot
    }

    [Fact]
    public void Clamp_ProtectsTaskDelayCast()
    {
        // 1e6 bots at 3_600_000 ms: bot 999999 would need 3.6e6 ms (fits int),
        // but a larger count x ramp product that exceeds int.MaxValue clamps.
        // 3_600_000 * 999_999 / 999_999 = 3_600_000, still fits; prove the clamp
        // only engages at true overflow: use a ramp that would overflow directly.
        Assert.Equal(int.MaxValue, Program.RampDelayMs(999_999, 1_000_000, int.MaxValue));
    }
}
