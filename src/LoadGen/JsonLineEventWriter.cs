using System.Text;

namespace SevenDTD.LoadGen;

/// <summary>Thread-safe JSON-lines sink shared by an observing bot cohort.</summary>
public sealed class JsonLineEventWriter : IDisposable
{
    readonly StreamWriter _writer;
    readonly object _gate = new();

    public JsonLineEventWriter(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new StreamWriter(new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        { AutoFlush = true };
    }

    public void Write(string json)
    {
        lock (_gate) _writer.WriteLine(json);
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
