using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Startup config gates for numeric CLI values. A value outside its documented
/// range must be rejected at startup with a named option, not silently change
/// run semantics (e.g. --min-pass-rate 15 gating every run to FAIL).
/// </summary>
public sealed class ConfigValidationTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(26900, true)]
    [InlineData(65535, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    public void Port_Range_IsEnforced(int port, bool valid)
        => Assert.Equal(valid, Program.IsValidPort(port));

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.95, true)]
    [InlineData(1.0, true)]
    [InlineData(-0.1, false)]
    [InlineData(1.1, false)]
    [InlineData(15.0, false)]
    public void MinPassRate_MustBeAFraction(double rate, bool valid)
        => Assert.Equal(valid, Program.IsValidMinPassRate(rate));

    [Theory]
    [InlineData("1", 1)]
    [InlineData("3600000", 3_600_000)]
    [InlineData("2147483647", int.MaxValue)]
    public void Timeout_PositiveMs_WithinIntCeiling_Parses(string raw, int expected)
    {
        Assert.True(Program.TryParseTimeoutMs(raw, out int ms));
        Assert.Equal(expected, ms);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("3600000.5")]
    // A 30-day soak overflows the int budget every downstream stage uses;
    // it must be rejected with its bound, not wrapped or crashed on.
    [InlineData("2592000000")]
    [InlineData("99999999999999999999")]
    public void Timeout_OutsideRange_IsRejected(string raw)
        => Assert.False(Program.TryParseTimeoutMs(raw, out _));

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("3600000", 3_600_000)]
    public void RampMs_WithinRange_Parses(string raw, int expected)
    {
        Assert.True(Program.TryParseRampMs(raw, out int ms));
        Assert.Equal(expected, ms);
    }

    [Theory]
    // A clamped ramp still ran the cohort, just not the one asked for.
    [InlineData("-1")]
    [InlineData("3600001")]
    [InlineData("abc")]
    public void RampMs_OutsideRange_IsRejected(string raw)
        => Assert.False(Program.TryParseRampMs(raw, out _));

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("1", 1.0)]
    // Parsed invariant: on a comma-decimal locale double.Parse reads "0,95" as
    // 0.95 and rejects the documented "0.95", so the same run config behaved
    // differently on two operator machines.
    [InlineData("0.95", 0.95)]
    public void MinPassRate_ParsesInvariant(string raw, double expected)
    {
        Assert.True(Program.TryParseMinPassRate(raw, out double rate));
        Assert.Equal(expected, rate);
    }

    [Theory]
    [InlineData("0,95")]
    [InlineData("15")]
    [InlineData("-0.1")]
    [InlineData("abc")]
    public void MinPassRate_NotAFraction_IsRejected(string raw)
        => Assert.False(Program.TryParseMinPassRate(raw, out _));

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("1000000", 1_000_000)]
    public void ClientId_WithinRange_Parses(string raw, int expected)
    {
        Assert.True(Program.TryParseClientId(raw, out int id));
        Assert.Equal(expected, id);
    }

    [Theory]
    // A negative base made `id % n` negative in the bind jitter, the retry
    // jitter and the spawn request's chunkViewDim; a base near int.MaxValue
    // wrapped base+count. Both are rejected, not clamped.
    [InlineData("-1")]
    [InlineData("1000001")]
    [InlineData("99999999999999999999")]
    [InlineData("abc")]
    public void ClientId_OutsideRange_IsRejected(string raw)
        => Assert.False(Program.TryParseClientId(raw, out _));

    [Theory]
    [InlineData(1, true)]
    [InlineData(64, true)]
    // A silent raise to 1 turned a LOADGEN_COUNT=0 typo into a one-bot run
    // whose summary read downstream as a measured result.
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Count_MustBePositive(int count, bool valid)
        => Assert.Equal(valid, Program.IsValidCount(count));
}
