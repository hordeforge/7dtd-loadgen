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
}
