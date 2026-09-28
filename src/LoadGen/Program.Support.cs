namespace SevenDTD.LoadGen;

// Helpers shared by more than one CLI mode: the serializer options the JSON
// artifacts share, and the bounded teardown wait. Anything a single mode calls
// alone belongs in that mode's own Program.*.cs file; the cross-cutting fault
// and artifact IO the networking layers also need lives in RunReport.cs.
public static partial class Program
{
    /// <summary>Shared JSON options for the run artifacts that exist (stats-json,
    /// run manifest): one instance so the artifact schemas serialize identically.</summary>
    static readonly System.Text.Json.JsonSerializerOptions ArtifactJsonOpts = new() { WriteIndented = true };

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
            Console.Error.WriteLine(RunReport.FaultLine($"{name} task faulted", ex.GetBaseException()));
        }
        catch (OperationCanceledException) { }
    }
}
