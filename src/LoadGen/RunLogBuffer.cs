namespace SevenDTD.LoadGen;

/// <summary>Thread-safe line buffer behind a run's <c>--log</c> artifact. A run's
/// log delegate is not single-threaded: the bot's own loop appends to it, and
/// the shared telnet provisioner appends the grant it executed from its worker
/// thread (each life hands the provisioner the same delegate). A bare
/// <see cref="List{T}"/> appended to from both loses entries and can publish a
/// half-written element, and enumerating it for the artifact throws "collection
/// was modified" while the second thread is still appending. Both operations are
/// serialized here, and reads take a snapshot so the writer iterates a stable
/// array rather than a live collection.</summary>
public sealed class RunLogBuffer
{
    readonly object _gate = new();
    readonly List<string> _lines = new();

    public void Add(string line)
    {
        lock (_gate) _lines.Add(line);
    }

    /// <summary>Every line recorded so far, as an independent copy.</summary>
    public string[] Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }
}
