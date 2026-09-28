using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

public sealed class RunReportTests
{
    [Fact]
    public void SafeText_ReplacesControlCharactersWithQuestionMarks()
    {
        // Console output and wire text are remote: a player name carrying a
        // newline or an escape sequence would otherwise forge log lines, or
        // repaint the operator's terminal, inside a transcript kept as evidence.
        string scrubbed = RunReport.SafeText("ok\r\nINFO\x1b[31mforged\x07");
        Assert.DoesNotContain('\n', scrubbed);
        Assert.DoesNotContain('\r', scrubbed);
        Assert.DoesNotContain('\x1b', scrubbed);
        Assert.DoesNotContain('\x07', scrubbed);
        Assert.Contains("ok??INFO?[31mforged?", scrubbed);
    }

    [Fact]
    public void SafeText_CapsTheSnippetWithoutSplittingASurrogatePair()
    {
        // Console text ends in emoji at the cut point often enough that a plain
        // slice would emit a lone surrogate and break the reader.
        string text = new string('x', RunReport.MaxScrubbedChars - 1) + "\U0001F600";
        string scrubbed = RunReport.SafeText(text);
        Assert.True(scrubbed.Length <= RunReport.MaxScrubbedChars);
        Assert.False(char.IsLowSurrogate(scrubbed[^1]));
    }

    [Fact]
    public void SafeText_RendersEmptyForNullOrEmpty()
    {
        Assert.Equal("", RunReport.SafeText(null));
        Assert.Equal("", RunReport.SafeText(""));
    }
}
