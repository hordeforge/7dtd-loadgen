using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// RelPos rotation packing: degrees map onto a 0..255 tick ring (256 ticks =
/// 360 degrees per the codec contract). The server interprets these shorts as
/// authoritative bot heading, so wrap, sign, and clamp behavior are wire
/// correctness, not style. Boundaries: exact quadrant ticks, negative and
/// multi-turn inputs normalize into range, and values that round up to 256
/// clamp to 255 instead of aliasing to 0.
/// </summary>
public sealed class PackAngle256Tests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(90f, 64)]
    [InlineData(180f, 128)]
    [InlineData(270f, 192)]
    public void QuadrantDegrees_MapToExactTicks(float degrees, short expected) =>
        Assert.Equal(expected, PackageCodec.PackAngle256(degrees));

    [Theory]
    [InlineData(-90f, 192)]   // -90 == 270
    [InlineData(-0.5f, 255)]  // wraps near full circle, must not go negative
    [InlineData(360f, 0)]
    [InlineData(720f, 0)]
    [InlineData(359.9f, 255)] // rounds to 256: clamped, not aliased to tick 0
    public void OutOfRangeInputs_NormalizeIntoRing(float degrees, short expected) =>
        Assert.Equal(expected, PackageCodec.PackAngle256(degrees));

    [Fact]
    public void AnyDegree_IsAlwaysInRange_0To255()
    {
        for (float d = -1440f; d <= 1440f; d += 7.3f)
        {
            short packed = PackageCodec.PackAngle256(d);
            Assert.InRange(packed, (short)0, (short)255);
        }
    }

    [Fact]
    public void FullTurn_AddsNothing()
    {
        // Rotating by a whole turn must not change the emitted tick.
        foreach (var d in new[] { 10f, 137f, 269.5f })
            Assert.Equal(PackageCodec.PackAngle256(d), PackageCodec.PackAngle256(d + 360f));
    }
}
