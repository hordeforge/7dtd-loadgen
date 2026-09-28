namespace SevenDTD.LoadGen;

// Artifact IO, task teardown, and the serializer options shared by the modes
// that emit JSON (join, self-test-join). Anything a single mode calls alone
// belongs in that mode's own Program.*.cs file.
public static partial class Program
{
    /// <summary>Shared JSON options for the run artifacts that exist (stats-json,
    /// run manifest): one instance so the artifact schemas serialize identically.</summary>
    static readonly System.Text.Json.JsonSerializerOptions ArtifactJsonOpts = new() { WriteIndented = true };

    /// <summary>One line describing a swallowed fault: context, type, message
    /// and the top stack frame. A long cohort run catches per-bot and per-task
    /// faults by design (one bad bot must not end the run), so the message is
    /// all that survives; the frame is what makes it diagnosable once the
    /// run's console is gone.</summary>
    internal static string FaultText(string context, Exception ex) =>
        $"{context}: {ex.GetType().Name}: {ex.Message}{TopFrame(ex)}";

    /// <summary>Leading stack frame as a single line, or empty when the
    /// exception carries none. One line, because a multi-line trace inside a
    /// timestamped log line breaks line-oriented parsers.</summary>
    internal static string TopFrame(Exception ex)
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
    internal static string FaultLine(string context, Exception ex) =>
        $"[{DateTime.UtcNow:O}] ERROR {FaultText(context, ex)}";

    /// <summary>Write line-oriented artifact text with LF terminators and no
    /// BOM, whatever the host's Environment.NewLine is. Client logs and JSONL
    /// sinks are read by the Python report lanes and diffed as evidence, so
    /// their bytes must not depend on the operating system that produced
    /// them.</summary>
    internal static void WriteLines(string path, IEnumerable<string> lines)
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
    internal static void WriteArtifact(string label, string path, Action write)
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

    /// <summary>Bounded wait for a background task to observe cancellation. A
    /// fault surfaces on stderr instead of vanishing: a dead spawner/sampler
    /// silently degrades the workload while the run still looks normal.</summary>
    internal static void AwaitTeardown(string name, Task? task)
    {
        if (task == null) return;
        try
        {
            task.Wait(2000);
        }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine(FaultLine($"{name} task faulted", ex.GetBaseException()));
        }
        catch (OperationCanceledException) { }
    }
}
