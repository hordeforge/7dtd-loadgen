namespace SevenDTD.LoadGen;

/// <summary>How a run reports faults and writes its artifacts: the stderr fault
/// line, the LF-only line sink, and the artifact writer. Cross-cutting, so the
/// networking and IO layers use it directly instead of reaching back into the
/// CLI entry point (<see cref="Program"/>), which depends on them in turn.</summary>
public static class RunReport
{
    /// <summary>Cap on a scrubbed remote-text snippet, in UTF-16 code units
    /// (<see cref="Snippet"/> trims a pair the cut would split).</summary>
    public const int MaxScrubbedChars = 160;

    // Line separator and paragraph separator. Written as code points because a
    // raw U+2028 in a C# character literal is a compiler newline, so the source
    // could not name the very character it has to reject.
    const char LineSeparator = '\u2028';
    const char ParagraphSeparator = '\u2029';

    /// <summary>Characters that must not survive into a line-oriented artifact.
    /// <see cref="char.IsControl"/> covers Cc and C1 (including U+0085 NEL) but
    /// not the Unicode line and paragraph separators: both are Zl and Zp, so
    /// char.IsControl is false for them, while Python's str.splitlines, which
    /// every report lane reads the log with, breaks a line on either one. Server
    /// chat carrying a line separator in a player name therefore forged a second
    /// log line in a transcript kept as run evidence, the exact failure the C0
    /// scrub exists to stop. The bidi controls cover the other half of the
    /// hazard: an override or isolate in server text reorders what the operator
    /// reads, so the name on screen is not the name in the file. Zero-width
    /// joiners and spaces stay; they cannot break a line, and U+200D is
    /// load-bearing in emoji sequences.</summary>
    internal static bool IsLogUnsafe(char c)
    {
        if (char.IsControl(c)) return true;
        if (c is LineSeparator or ParagraphSeparator) return true;
        return c is '\u061C' or '\u200E' or '\u200F'   // ALM, LRM, RLM
            || (c >= '\u202A' && c <= '\u202E')   // bidi embedding and override
            || (c >= '\u2066' && c <= '\u2069');  // bidi isolates
    }

    /// <summary>Remote text (wire packages, console output, player names) with
    /// every line-forging or display-reordering character replaced by '?'. The
    /// rest is preserved, non-ASCII letters and emoji included, so name and
    /// death-word matching still see the text the server sent.</summary>
    public static string ScrubLineUnsafe(string s)
    {
        int first = 0;
        while (first < s.Length && !IsLogUnsafe(s[first])) first++;
        if (first == s.Length) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        sb.Append(s, 0, first);
        for (int i = first; i < s.Length; i++)
            sb.Append(IsLogUnsafe(s[i]) ? '?' : s[i]);
        return sb.ToString();
    }

    /// <summary>Remote text (wire packages, console output, player names) as
    /// one printable log line: line-forging characters become '?' and the
    /// snippet is capped. A newline or an escape sequence inside server text
    /// would otherwise forge log lines, or repaint the operator's terminal, in a
    /// transcript that is kept as run evidence.</summary>
    public static string SafeText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(Math.Min(s.Length, MaxScrubbedChars));
        foreach (char c in s)
        {
            // Stop at the snippet cap so a hostile oversized string cannot make
            // the scrub loop itself the cost; Snippet below still trims a cut
            // that lands inside a surrogate pair.
            if (sb.Length >= MaxScrubbedChars) break;
            sb.Append(IsLogUnsafe(c) ? '?' : c);
        }
        return Snippet(sb.ToString(), MaxScrubbedChars);
    }

    /// <summary>Truncate for logging without splitting a surrogate pair: chat
    /// text is server-controlled and may end in emoji at the cut point.</summary>
    public static string Snippet(string s, int maxChars)
    {
        if (maxChars <= 0) return "";
        if (s.Length <= maxChars) return s;
        int len = maxChars;
        if (char.IsHighSurrogate(s[len - 1]))
            len--;
        return s[..len];
    }

    /// <summary>One line describing a swallowed fault: context, type, message
    /// and the top stack frame. A long cohort run catches per-bot and per-task
    /// faults by design (one bad bot must not end the run), so the message is
    /// all that survives; the frame is what makes it diagnosable once the
    /// run's console is gone.</summary>
    public static string FaultText(string context, Exception ex) =>
        $"{context}: {ex.GetType().Name}: {ex.Message}{TopFrame(ex)}";

    /// <summary>Leading stack frame as a single line, or empty when the
    /// exception carries none. One line, because a multi-line trace inside a
    /// timestamped log line breaks line-oriented parsers.</summary>
    static string TopFrame(Exception ex)
    {
        string? trace = ex.StackTrace;
        if (string.IsNullOrEmpty(trace)) return "";
        int end = trace.IndexOf('\n');
        return (end < 0 ? trace : trace[..end]).Trim() is { Length: > 0 } first
            ? $" at {first}"
            : "";
    }

    /// <summary>One stderr fault line in the shape every other fault in the
    /// client uses: UTC timestamp, ERROR level, then the fault. A teardown
    /// fault that arrives without those two fields is indistinguishable from
    /// a bot's own log line in a long cohort transcript, and the level is what
    /// a grep for the run's errors keys on.</summary>
    public static string FaultLine(string context, Exception ex) =>
        $"[{DateTime.UtcNow:O}] ERROR {FaultText(context, ex)}";

    /// <summary>Write line-oriented artifact text with LF terminators and no
    /// BOM, whatever the host's Environment.NewLine is. Client logs and JSONL
    /// sinks are read by the Python report lanes and diffed as evidence, so
    /// their bytes must not depend on the operating system that produced
    /// them.</summary>
    public static void WriteLines(string path, IEnumerable<string> lines)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var w = new StreamWriter(fs, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            NewLine = "\n",
        };
        foreach (string line in lines) w.WriteLine(line);
    }

    /// <summary>Write a run artifact (log/stats-json/run manifest) without letting
    /// an IO failure mask the run's exit code: the measurement finished, so its
    /// gate result must still propagate. The artifact's parent directory is
    /// created first, so every sink accepts a nested path uniformly. Evidence
    /// loss goes to stderr, loudly.</summary>
    public static void WriteArtifact(string label, string path, Action write)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            write();
            Console.WriteLine($"{label}: {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(FaultLine($"writing {label} {path}", ex));
        }
    }
}
