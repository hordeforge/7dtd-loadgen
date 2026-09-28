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

    [Fact]
    public void BotModeAlias_AttributesLikeTheFlagItAliases()
    {
        // --help documents --bot-mode as an alias for --mode and the parser
        // reads both from one branch, so --bot-mode has to be in the
        // value-flag set too. Without it a bad value there blamed no flag and
        // fell back to "bad argument value", sending the operator to scan
        // every other numeric flag on a cohort command line.
        var args = new[] { "--join", "--bot-mode", "abc" };
        Assert.Equal("--bot-mode", Program.BadValueFlag(args, FormatMessage));
    }
}
