namespace SevenDTD.LoadGen;

/// <summary>How a run reports faults and writes its artifacts: the stderr fault
/// line, the LF-only line sink, and the artifact writer. Cross-cutting, so the
/// networking and IO layers use it directly instead of reaching back into the
/// CLI entry point (<see cref="Program"/>), which depends on them in turn.</summary>
public static class RunReport
{
    /// <summary>Cap on a scrubbed remote-text snippet, in chars.</summary>
    public const int MaxScrubbedChars = 160;

    /// <summary>Remote text (wire packages, console output, player names) as
    /// one printable log line: control characters become '?' and the snippet is
    /// capped. A newline or an escape sequence inside server text would
    /// otherwise forge log lines, or repaint the operator's terminal, in a
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
            sb.Append(char.IsControl(c) ? '?' : c);
        }
        return Snippet(sb.ToString(), MaxScrubbedChars);
    }

    /// <summary>Truncate for logging without splitting a surrogate pair: chat
    /// text is server-controlled and may end in emoji at the cut point.</summary>
    public static string Snippet(string s, int maxChars)
    {
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
