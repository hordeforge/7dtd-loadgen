using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Bench window slicing + active-cohort curve. The window is
/// [warmupMs, warmupMs + windowMs) after the cohort start; events outside the
/// window are not counted. Uses the internal elapsed-ms test seam so the
/// boundaries are deterministic.
/// </summary>
public sealed class BenchClockTests
{
    private static BenchClock Clock(int warmup, int window, long elapsedMs) =>
        new(warmup, window, () => elapsedMs);

    [Fact]
    public void WindowDisabled_NeverCounts()
    {
        var c = Clock(100, 0, 50); // windowMs=0 => disabled
        c.OnAction();
        c.OnDeath();
        c.OnRespawn();
        Assert.False(c.WindowEnabled);
        Assert.False(c.InWindow);
        Assert.Equal((0, 0, 0), c.WindowCounts);
    }

    [Fact]
    public void WindowSlicing_CountsOnlyInside()
    {
        var c = Clock(100, 200, 0);
        Assert.False(c.InWindow);
        c.OnAction();                    // t=0, before warm-up: not counted

        // Advance the fake clock inside the window.
        var inside = Clock(100, 200, 150);
        inside.OnAction();
        inside.OnAction();
        inside.OnDeath();
        Assert.True(inside.InWindow);
        Assert.Equal((2, 1, 0), inside.WindowCounts);

        // At exactly the window end the interval is closed: not counted.
        var atEnd = Clock(100, 200, 300);
        atEnd.OnAction();
        Assert.False(atEnd.InWindow);
        Assert.Equal((0, 0, 0), atEnd.WindowCounts);

        // Past the end: not counted.
        var past = Clock(100, 200, 500);
        past.OnAction();
        past.OnRespawn();
        Assert.Equal((0, 0, 0), past.WindowCounts);
    }

    [Fact]
    public void WindowBounds_ReflectConfig()
    {
        var c = Clock(30000, 60000, 0);
        Assert.Equal((30000L, 90000L), c.WindowBounds);
        var disabled = Clock(0, 0, 0);
        Assert.Equal((0L, 0L), disabled.WindowBounds);
    }

    [Fact]
    public void ActiveCurve_TracksMinMaxAndEdges()
    {
        long now = 0;
        var c = new BenchClock(1500, 2000, () => now);
        c.SampleActive(0);          // t=0
        now = 1000; c.SampleActive(4);   // before window start
        now = 2000; c.SampleActive(16);  // inside the window
        now = 2000; c.SampleActive(16);  // same second: replaces, no duplicate
        now = 4000; c.SampleActive(8);   // past the end (ramp-down)
        Assert.Equal(0, c.ActiveMin);
        Assert.Equal(16, c.ActiveMax);
        Assert.Equal(4, c.ActiveAtWindowStart);  // last sample at/before warmup
        Assert.Equal(8, c.ActiveAtWindowEnd);    // last sample overall
        var curve = c.ActiveCurve();
        Assert.Equal(4, curve.Count);            // duplicate same-second replaced
        Assert.Equal((0L, 0), curve[0]);
        Assert.Equal((2000L, 16), curve[2]);
    }

    [Fact]
    public void ActiveCurve_BeyondIntMaxMs_StaysMonotonic()
    {
        // Multi-day soak processes sample past int.MaxValue ms (~24.9 days);
        // timestamps must keep their magnitude and order, not wrap negative.
        long late = (long)int.MaxValue + 5_000;
        long now = 0;
        var c = new BenchClock(0, 60_000, () => now);
        c.SampleActive(2);
        now = late;
        c.SampleActive(3);
        var curve = c.ActiveCurve();
        Assert.Equal(2, curve.Count);            // far apart: no same-second dedup
        Assert.Equal((0L, 2), curve[0]);
        Assert.Equal((late, 3), curve[^1]);      // positive and ordered
    }

    [Fact]
    public void InWindow_LongEndBound_DoesNotWrapNegative()
    {
        // warmup+window exceeds int.MaxValue ms: the end bound must widen to
        // long before adding (int sum wraps negative and kills the window).
        long now = (long)int.MaxValue + 60_000;
        var c = new BenchClock(int.MaxValue - 30_000, 120_000, () => now);
        Assert.True(c.InWindow);
        Assert.Equal((long)(int.MaxValue - 30_000), c.WindowBounds.StartMs);
        Assert.Equal((long)int.MaxValue + 90_000, c.WindowBounds.EndMs);
    }

    [Fact]
    public void ActiveAtWindowStart_NoSampleYet_IsZero()
    {
        var c = Clock(100, 200, 0);
        Assert.Equal(0, c.ActiveAtWindowStart);
        Assert.Equal(0, c.ActiveAtWindowEnd);
        Assert.Equal(0, c.ActiveMin);
    }
}
