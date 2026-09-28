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
}
