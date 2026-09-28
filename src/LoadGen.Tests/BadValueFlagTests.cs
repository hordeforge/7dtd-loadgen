using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Attribution of a numeric parse failure back to the flag that caused it.
/// A usage error that does not name its flag forces the operator to scan every
/// numeric flag in a cohort command line to find the typo.
/// </summary>
public sealed class BadValueFlagTests
{
    const string FormatMessage =
        "The input string 'abc' was not in a correct format.";
    const string OverflowMessage =
        "Value was either too large or too small for an Int32.";

    [Fact]
    public void QuotedValue_AttributesToThePrecedingValueFlag()
    {
        var args = new[] { "--join", "--host", "127.0.0.1", "--count", "abc" };
        Assert.Equal("--count", Program.BadValueFlag(args, FormatMessage));
    }

    [Fact]
    public void OverflowWithoutAQuotedValue_AttributesToTheOffendingFlag()
    {
        var args = new[] { "--join", "--port", "99999999999", "--count", "8" };
        Assert.Equal("--port", Program.BadValueFlag(args, OverflowMessage));
    }

    [Fact]
    public void StandaloneSwitch_IsNotMistakenForAValueFlag()
    {
        // --join is a switch: "join" following it is not a value to blame.
        var args = new[] { "--join", "--port", "99999999999" };
        Assert.Equal("--port", Program.BadValueFlag(args, OverflowMessage));
    }

    [Fact]
    public void UnparseableMessage_ReturnsNullRatherThanGuessing()
    {
        var args = new[] { "--name", "abc", "--port", "26902" };
        Assert.Null(Program.BadValueFlag(args, "something else went wrong"));
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--count")]
    [InlineData("--min-pass-rate")]
    [InlineData("--profile")]
    public void TrailingValueFlag_IsReportedRatherThanRunOnTheDefault(string flag)
    {
        // Every lane's parser reads a value only when a token follows the flag,
        // so `--join --port` used to start a run on the default port and exit on
        // the normal gate: the one malformed argv that changed the workload
        // without an error.
        var args = new[] { "--join", flag };
        Assert.Equal(flag, Program.MissingFlagValue(args));
    }

    [Fact]
    public void ValueFlagWithAValue_IsNotAMissingValue()
    {
        Assert.Null(Program.MissingFlagValue(new[] { "--join", "--port", "26902" }));
    }

    [Theory]
    [InlineData("--join")]
    [InlineData("--quiet")]
    [InlineData("--no-spawn-zombies")]
    public void TrailingSwitch_IsNotAValueFlag(string flag)
    {
        Assert.Null(Program.MissingFlagValue(new[] { "--join", flag }));
    }

    [Fact]
    public void EmptyArgv_IsNotAMissingValue()
    {
        Assert.Null(Program.MissingFlagValue(Array.Empty<string>()));
    }
}
