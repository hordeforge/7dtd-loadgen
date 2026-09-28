using System.Text;

namespace SevenDTD.LoadGen;

/// <summary>Thread-safe JSON-lines sink shared by an observing bot cohort.</summary>
public sealed class JsonLineEventWriter : IDisposable
{
    // Event lines are small and every bot in the cohort shares this one sink,
    // so AutoFlush turned each line into its own write() syscall taken under the
    // cohort-wide lock. At the line rates the observer lanes produce that is the
    // dominant cost of the sink, and the syscall rate is proportional to the bot
    // count. Batching on an interval keeps the evidence timely (bounded loss
    // window on a hard kill) while collapsing N syscalls into one per window.
    internal const int FlushIntervalMs = 250;
    static readonly long FlushIntervalTicks =
        (long)(FlushIntervalMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);

    readonly StreamWriter _writer;
    readonly object _gate = new();
    long _lastFlushTicks;

    public JsonLineEventWriter(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new StreamWriter(new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
            // LF, not Environment.NewLine: the JSONL sink is read by the
            // report lanes and diffed as run evidence, so its bytes must not
            // depend on the host OS.
            NewLine = "\n",
        };
        _lastFlushTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public void Write(string json)
    {
        lock (_gate)
        {
            _writer.WriteLine(json);
            // Stopwatch.GetTimestamp is a single rdtsc-scale read; the syscall it
            // replaces costs orders of magnitude more, so the check is noise
            // next to what it saves.
            if (System.Diagnostics.Stopwatch.GetTimestamp() - _lastFlushTicks < FlushIntervalTicks)
                return;
            _writer.Flush();
            _lastFlushTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Push every buffered line to the file now (end of a run phase).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _writer.Flush();
            _lastFlushTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // Dispose runs after the run's exit code is decided; a throw here
            // (final flush on a full disk) would replace that code with a crash
            // trace at process exit. The emit path already latches per-line
            // faults, so this only keeps teardown from masking a finished run.
            try { _writer.Dispose(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[{DateTime.UtcNow:O}] ERROR closing events sink: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
