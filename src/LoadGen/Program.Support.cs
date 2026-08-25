namespace SevenDTD.LoadGen;

public static partial class Program
{
    /// <summary>Shared JSON options for every run artifact (stats-json, run
    /// manifest): one instance so the artifact schemas serialize identically.</summary>
    static readonly System.Text.Json.JsonSerializerOptions ArtifactJsonOpts = new() { WriteIndented = true };

    /// <summary>Workload identity recorded in stats-json and the run manifest
    /// (README: "the run manifest records seed, dynamite cap, and spawn
    /// configuration for workload comparability").</summary>
    internal static Dictionary<string, object?> WorkloadBlock(
        int seed, int actions, int paceMs, int count, int concurrency, int clientIdBase,
        int rampMs, int maxDynamitePerLife, bool respawn, int maxLives,
        bool spawnZombies, bool killFallback, string spawnEntity, int spawnPerPlayer,
        int spawnEveryMs, int hordeEveryMs, int hordeWaves) => new()
        {
            ["seed"] = seed,
            ["actions"] = actions,
            ["paceMs"] = paceMs,
            ["count"] = count,
            ["concurrency"] = concurrency,
            ["clientIdBase"] = clientIdBase,
            ["rampMs"] = rampMs,
            ["maxDynamitePerLife"] = maxDynamitePerLife,
            ["respawn"] = respawn,
            ["maxLives"] = maxLives,
            ["spawnZombies"] = spawnZombies,
            ["killFallback"] = killFallback,
            ["spawnEntity"] = spawnEntity,
            ["spawnPerPlayer"] = spawnPerPlayer,
            ["spawnEveryMs"] = spawnEveryMs,
            ["hordeEveryMs"] = hordeEveryMs,
            ["hordeWaves"] = hordeWaves,
        };

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

    /// <summary>Cancellable sleep that reports whether to keep looping.
    /// .Wait() wraps delay cancellation in AggregateException, which neither an
    /// OperationCanceledException catch nor the token-gated fault catch sees:
    /// letting it propagate would fault the pressure task on every clean
    /// teardown and make AwaitTeardown log a spurious ERROR into the run log.</summary>
    static bool NappableDelay(int ms, CancellationToken ct)
    {
        try { Task.Delay(ms, ct).Wait(); }
        catch { /* cancellation (or a rare race); the token decides */ }
        return !ct.IsCancellationRequested;
    }

    /// <summary>Periodic telnet pressure loop shared by the zombie trickle and
    /// wandering hordes: one fresh telnet session per wave (long sessions drop
    /// half-open sockets), fixed backoff on faults, ends with cancellation.</summary>
    internal static Task RunTelnetPressureLoop(
        string label, CancellationToken ct,
        int startDelayMs, int intervalMs, int errorBackoffMs,
        Func<TelnetAdmin> createAdmin, Action<TelnetAdmin> wave)
        => Task.Run(() =>
        {
            if (!NappableDelay(startDelayMs, ct)) return;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var admin = createAdmin();
                    if (admin.Connect())
                        wave(admin);
                    if (!NappableDelay(intervalMs, ct)) break;
                }
                catch (OperationCanceledException) { break; }
                // A fault observed while shutting down is teardown, not a telnet
                // error: gate on the token so a stop never waits out the backoff.
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[{DateTime.UtcNow:O}] TELNET {label} err: {ex.Message}");
                    if (!NappableDelay(errorBackoffMs, ct)) break;
                }
            }
        });
}
