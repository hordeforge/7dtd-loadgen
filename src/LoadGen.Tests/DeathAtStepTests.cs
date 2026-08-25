using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Client-death window placement: the forced-death sequence starts two steps
/// before the planned end (the 4/5 term only matters below ten steps, where
/// n-2 would fire earlier than 80%). The multiply must widen to long first:
/// --actions parses up to int.MaxValue unclamped, and above 536870911 the int
/// product n*4 wraps negative, silently relying on the Max to mask the wrap.
/// </summary>
public sealed class DeathAtStepTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(100)]
    [InlineData(1000)]
    public void RealisticRuns_DeathWindowSitsTwoStepsBeforeEnd(int plannedSteps)
        => Assert.Equal(plannedSteps - 2, ActionLoop.DeathAtStep(plannedSteps));

    [Theory]
    [InlineData(int.MaxValue / 2)]
    [InlineData(int.MaxValue)]
    public void HugeRuns_NeverWrapNegative(int plannedSteps)
    {
        int deathAt = ActionLoop.DeathAtStep(plannedSteps);
        // The old int product wrapped negative here; the window must stay a
        // sane step index inside the plan regardless.
        Assert.InRange(deathAt, 0, plannedSteps);
        Assert.Equal(plannedSteps - 2, deathAt);
    }

    [Fact]
    public void EveryPlausibleScale_ResultIsInsideThePlan_AndLateEnough()
    {
        for (long n = 12; n <= 1_000_000_000; n = n * 2 + 1)
        {
            int deathAt = ActionLoop.DeathAtStep((int)n);
            long eightyPct = 4 * n / 5;
            Assert.InRange(deathAt, (int)Math.Min(eightyPct, n - 2), (int)(n - 2));
        }
    }
}
