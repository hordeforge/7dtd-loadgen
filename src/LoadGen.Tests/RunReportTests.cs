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

    [Fact]
    public void SafeText_ReplacesTheSeparatorsSplitlinesBreaksOn()
    {
        // U+2028 and U+2029 are Zl and Zp, so char.IsControl is false for
        // both, and Python's str.splitlines (what the report lanes read the log
        // with) breaks a line on either. A player name carrying one forged a
        // second log line inside a transcript kept as run evidence.
        string scrubbed = RunReport.SafeText("REFake1 died\u2028PASS joined entity=9999\u2029end");
        Assert.Equal("REFake1 died?PASS joined entity=9999?end", scrubbed);
    }

    [Fact]
    public void SafeText_ReplacesBidiControlsAndKeepsTheSurroundingText()
    {
        // A bidi override or isolate reorders what the operator reads, so the
        // name on screen is not the name in the file.
        string scrubbed = RunReport.SafeText("Zoe\u202Edrowssap");
        Assert.Equal("Zoe?drowssap", scrubbed);
        Assert.DoesNotContain('\u202E', scrubbed);
    }

    [Fact]
    public void SafeText_KeepsTheZeroWidthJoinerInAnEmojiSequence()
    {
        // U+200D joins the emoji in a family sequence; scrubbing it would
        // rewrite the one pictograph family the chat actually carries.
        string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        Assert.Equal(family, RunReport.SafeText(family));
    }

    [Fact]
    public void SafeText_KeepsNonAsciiLettersForNameMatching()
    {
        Assert.Equal("Zoë 日本", RunReport.SafeText("Zoë 日本"));
    }

    [Fact]
    public void Event_CarriesTheUtcStampAndTheLevel()
    {
        // Every line of a run is built here: a module that formats its own line
        // puts one into the transcript with no timestamp and no level, which a
        // time-ordered read and a grep on ERROR both miss.
        string line = RunReport.Event("WARN", "console unreachable");
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T[0-9:.]+Z\] WARN console unreachable$", line);
    }

    [Fact]
    public void FaultLine_KeepsTheTimestampLevelAndFaultShape()
    {
        string line = RunReport.FaultLine("join session", new InvalidOperationException("boom"));
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T[0-9:.]+Z\] ERROR join session: InvalidOperationException: boom", line);
    }

    [Fact]
    public void Snippet_AtZeroCapIsEmptyRatherThanOutOfRange()
    {
        // A zero cap used to index s[-1] and throw, taking the caller with it.
        Assert.Equal("", RunReport.Snippet("abc", 0));
        Assert.Equal("", RunReport.Snippet("", 0));
    }
}
