namespace SevenDTD.LoadGen;

// Artifact IO, task teardown, and the serializer options shared by every mode
// (probe, join, self-test, self-test-join). Anything a single mode calls alone
// belongs in that mode's own Program.*.cs file.
public static partial class Program
{
    /// <summary>Shared JSON options for every run artifact (stats-json, run
    /// manifest): one instance so the artifact schemas serialize identically.</summary>
    static readonly System.Text.Json.JsonSerializerOptions ArtifactJsonOpts = new() { WriteIndented = true };

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
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:O}] ERROR writing {label} {path}: {ex.GetType().Name}: {ex.Message}");
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
            var baseEx = ex.GetBaseException();
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:O}] ERROR {name} task faulted: {baseEx.GetType().Name}: {baseEx.Message}");
        }
        catch (OperationCanceledException) { }
    }
}
