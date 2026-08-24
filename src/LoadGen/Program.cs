using System.Diagnostics;
using LiteNetLib;

namespace SevenDTD.LoadGen;

/// <summary>
/// 7DTD load-test client: LiteNetLib probe, full join path, bot walk/death/respawn.
/// </summary>
public static class Program
{
    /// <summary>No-op logger so game LiteNetLib never calls UnityEngine.Debug under pure .NET.</summary>
    sealed class NullNetLogger : INetLogger
    {
        public void WriteNet(NetLogLevel level, string str, params object[] args) { }
    }

    /// <summary>
    /// Stagger delay for bot i of count under --ramp-ms. Linear ramp across the
    /// cohort; first bot always starts at 0. Clamped so the Task.Delay(int)
    /// cast at scale cannot overflow (see --ramp-ms parse). Validated live
    /// 2026-08-10: 24 bots at 3000 ms ramp avoided the stock LiteNetLib
    /// join-churn race entirely (0 drops vs 302 non-ramped).
    /// </summary>
    public static int RampDelayMs(int botIndex, int count, int rampMs)
    {
        if (rampMs <= 0 || count <= 1) return 0;
        return (int)Math.Min(int.MaxValue, (long)botIndex * rampMs / (count - 1));
    }

    /// <summary>
    /// Join gate: pass when the successful-client rate meets --min-pass-rate.
    /// The 1e-9 epsilon absorbs float division rounding at exact equality.
    /// </summary>
    public static bool JoinGatePass(int passed, int total, double minPassRate)
    {
        double rate = total == 0 ? 0 : (double)passed / total;
        return rate + 1e-9 >= minPassRate;
    }

    /// <summary>Valid UDP/TCP port range for --port/--telnet-port.</summary>
    public static bool IsValidPort(int port) => port >= 1 && port <= 65535;

    /// <summary>Consumer-facing build identity, e.g. "7dtd-loadgen 0.1.0".
    /// Backed by &lt;Version&gt; in LoadGen.csproj (see test_release_contract.py).</summary>
    public static string VersionLine()
    {
        var v = typeof(Program).Assembly.GetName().Version;
        return $"7dtd-loadgen {(v is null ? "unknown" : v.ToString(3))}";
    }

    /// <summary>--min-pass-rate is a client fraction; outside [0,1] the gate
    /// silently loses meaning (always-fail or always-pass).</summary>
    public static bool IsValidMinPassRate(double rate) => !double.IsNaN(rate) && rate >= 0.0 && rate <= 1.0;

    /// <summary>Fail fast on an out-of-range configuration value instead of a
    /// confusing mid-run failure. Exit code 2 matches bad argument values.</summary>
    internal static int InvalidArg(string flag, string value, string requirement)
    {
        Console.Error.WriteLine($"FAIL: invalid {flag} '{value}': {requirement} (see --help)");
        return 2;
    }

    static int Main(string[] args)
    {
        // Game LiteNetLib logs via UnityEngine.Debug when Logger is null; pure .NET crashes
        // with "ECall methods must be packaged into a system module" on bind/socket errors.
        NetDebug.Logger = new NullNetLogger();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => GameJoinClient.DisconnectAllActive();

        Console.CancelKeyPress += (_, _) => GameJoinClient.DisconnectAllActive();

        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return 0;
        }

        if (args.Any(a => a is "-V" or "--version"))
        {
            Console.WriteLine(VersionLine());
            return 0;
        }

        if (args.Any(a => a == "--golden-wire"))
        {
            var err = PackageCodec.AssertGoldenWireLayouts();
            if (err != null)
            {
                Console.WriteLine($"FAIL golden-wire: {err}");
                return 1;
            }
            Console.WriteLine(
                "PASS golden-wire: " +
                $"PosAndRot body={PackageCodec.GoldenBodySize.EntityPosAndRotNoQ} " +
                $"RelPos body={PackageCodec.GoldenBodySize.EntityRelPosAndRotNoQ} (Int16 rot) " +
                $"AliveFlags body={PackageCodec.GoldenBodySize.EntityAliveFlags}");
            return 0;
        }

        string mode = "probe";
        if (args.Any(a => a == "--join")) mode = "join";
        if (args.Any(a => a == "--self-test")) mode = "self-test";
        if (args.Any(a => a == "--self-test-join")) mode = "self-test-join";

        try
        {
            return mode switch
            {
                "self-test-join" => RunSelfTestJoin(args),
                "self-test" => SelfTest.Run(args),
                "join" => RunJoin(args),
                _ => RunProbe(args),
            };
        }
        // CLI boundary: a malformed numeric flag (--count abc, --port 99999999999)
        // must fail as a clean usage error, not an unhandled-exception stack
        // trace. Parse sites use int/double.Parse, whose failure modes are
        // exactly these two types; wider exception families stay visible.
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            Console.Error.WriteLine($"FAIL: bad argument value: {ex.Message} (see --help)");
            return 2;
        }
    }

    /// <summary>Write a run artifact (log/stats-json/run manifest) without letting
    /// an IO failure mask the run's exit code: the measurement finished, so its
    /// gate result must still propagate. Evidence loss goes to stderr, loudly.</summary>
    internal static void WriteArtifact(string label, string path, Action write)
    {
        try
        {
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

    static int RunSelfTestJoin(string[] args)
    {
        int actions = 24;
        int seed = 7;
        string? logPath = null;
        string? runManifestPath = null;
        string scenarioId = Environment.GetEnvironmentVariable("LOADGEN_SCENARIO_ID") ?? "re-selftest-client-path";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--actions" && i + 1 < args.Length) actions = int.Parse(args[++i]);
            else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
            else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
            else if (args[i] == "--run-manifest" && i + 1 < args.Length) runManifestPath = args[++i];
            else if (args[i] == "--scenario-id" && i + 1 < args.Length) scenarioId = args[++i];
        }
        if (actions < 6) actions = 6;

        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }

        Log($"[{DateTime.UtcNow:O}] self-test-join actions={actions} seed={seed}");
        int rc = GameJoinClient.RunSelfTestJoin(actions, seed, Log, out var sm);
        Log($"SUMMARY stage={sm.Stage} joined={sm.IsJoined} mode={sm.BotModeName} " +
            $"walks={sm.WalkActions} jumps={sm.JumpActions} crouch={sm.CrouchActions} aim={sm.AimActions} " +
            $"turn={sm.TurnActions} chat={sm.ChatActions} attack={sm.AttackActions} " +
            $"drowns={sm.DrownActions} suicides={sm.SuicideActions} killed={sm.KilledActions} " +
            $"deaths={sm.DeathCount} respawns={sm.RespawnCount} " +
            $"died={sm.Died} cause={sm.DeathCause} entity={sm.EntityId} fail={sm.FailReason ?? "none"}");
        if (!string.IsNullOrEmpty(logPath))
            WriteArtifact("log", logPath, () => File.WriteAllLines(logPath, lines.Concat(sm.Log)));
        if (rc == 0)
            Log("PASS: self-test-join joined + actions");
        else
            Log("FAIL: self-test-join");

        if (!string.IsNullOrEmpty(runManifestPath))
        {
            var run = new Dictionary<string, object?>
            {
                ["schema"] = "7dtd.loadgen.run.v1",
                ["kind"] = "self-test-join",
                ["scenarioId"] = scenarioId,
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["rc"] = rc,
                ["pass"] = rc == 0,
                ["actions"] = actions,
                ["seed"] = seed,
                ["stage"] = sm.Stage.ToString(),
                ["joined"] = sm.IsJoined,
                ["walks"] = sm.WalkActions,
                ["deaths"] = sm.DeathCount,
                ["respawns"] = sm.RespawnCount,
                ["product"] = new Dictionary<string, object?>
                {
                    ["name"] = "RealEarth",
                    ["priorityFocus"] = "P0-P1",
                    ["offlineGate"] = true,
                },
            };
            WriteArtifact("run_manifest", runManifestPath, () =>
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(runManifestPath));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(
                    runManifestPath,
                    System.Text.Json.JsonSerializer.Serialize(run, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            });
        }
        return rc;
    }

    /// <summary>Aggregate action/death counters for stats-json. Longs because the
    /// sum across up to 1000 bots on a multi-day run exceeds int.MaxValue (int
    /// Sum wraps silently under unchecked arithmetic).</summary>
    readonly record struct CohortCounters(
        long Walks, long Jumps, long Crouches, long Aims, long Turns, long Strafes,
        long Looks, long Chats, long Breaks, long Attacks, long Drowns, long Suicides,
        long Killed, int DiedClients, long TotalDeaths, long TotalRespawns, long TotalRejoins)
    {
        public static CohortCounters FromState(JoinStateMachine sm) => new(
            sm.WalkActions, sm.JumpActions, sm.CrouchActions, sm.AimActions, sm.TurnActions,
            sm.StrafeActions, sm.LookActions, sm.ChatActions, sm.BreakBlockActions,
            sm.AttackActions, sm.DrownActions, sm.SuicideActions, sm.KilledActions,
            sm.Died ? 1 : 0, sm.DeathCount, sm.RespawnCount, sm.RejoinCount);
    }

    static int RunJoin(string[] args)
    {
        var opt = new GameJoinClient.Options();
        // Secrets resolve from the environment when the flag is absent, so
        // operators and runners can keep credentials out of argv (ps-visible).
        // An explicit --key/--password/--telnet-password always wins.
        opt.Password = Environment.GetEnvironmentVariable("LOADGEN_KEY") is { Length: > 0 } envKey ? envKey : opt.Password;
        string? logPath = null;
        string? statsJsonPath = null;
        string? runManifestPath = null;
        string? eventsJsonlPath = null;
        var observedCvars = new List<string>();
        var observedBuffs = new List<string>();
        string scenarioId = Environment.GetEnvironmentVariable("LOADGEN_SCENARIO_ID") ?? "";
        int joinRampMs = 0;
        int count = 1;
        int concurrency = 0;
        double minPassRate = 1.0;
        bool modeSet = false;
        bool deathSet = false;
        bool timeoutSet = false;
        // Height-test worlds have empty prefabs → no natural zeds; telnet-spawn by default on join.
        bool spawnZombies = true;
        bool killFallback = true;
        string telnetHost = "127.0.0.1";
        int telnetPort = 8081;
        // Test-only lab default; override per environment via the env var or the
        // flag (AGENTS.md rule 4: prefer env / local config).
        string telnetPassword =
            Environment.GetEnvironmentVariable("LOADGEN_TELNET_PASSWORD") is { Length: > 0 } envPw
                ? envPw
                : "retest";
        int spawnEveryMs = 20_000;
        int spawnPerPlayer = 4;
        string spawnEntity = "zombieBoe";
        // Benchmark mode: joins settle during warm-up, the measurement window is
        // [warmupMs, warmupMs+windowMs) after the cohort start, and the stats-json
        // gets a bench block (window counts + active-client curve). 0 = disabled.
        int benchWarmupMs = 30_000;
        int benchWindowMs = 0;
        // Wandering hordes: periodic scout-horde bursts that spawn at distance and
        // path in as a group. 0 = off. Slower cadence than the per-player trickle.
        int hordeEveryMs = 0;
        int hordeWaves = 3;
        // Weighted per-bot mode mix, e.g. "traverse:35,combat:20,bait:15". Empty
        // -> whole cohort uses opt.Mode. Assigned deterministically by client id
        // so the profile is repeatable.
        var botMix = new List<(ActionLoop.BotMode mode, int weight)>();

        // Named workload profiles: preset cohort defaults applied before the arg
        // loop so an explicit flag on the same command line always overrides the
        // profile.
        string profile = "";
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--profile" && i + 1 < args.Length) profile = args[++i];
        switch (profile)
        {
            case "probe": // one bot, bounded steps, no death: join + handshake health
                count = 1; concurrency = 1; opt.ActionCount = 20;
                opt.TimeoutMs = 120_000; timeoutSet = true;
                deathSet = true; opt.Death = ActionLoop.DeathMethod.None;
                break;
            case "join-burst": // many simultaneous joins, short steps, no death
                count = 24; concurrency = 24; opt.ActionCount = 10;
                opt.TimeoutMs = 120_000; timeoutSet = true;
                deathSet = true; opt.Death = ActionLoop.DeathMethod.None;
                break;
            case "steady-wander": // endless wander for a soak window
                count = 8; concurrency = 8; opt.ActionCount = 0;
                opt.TimeoutMs = 900_000; timeoutSet = true;
                break;
            case "death-soak": // combat + self-kill + respawn loop
                count = 6; concurrency = 6; opt.ActionCount = 60;
                opt.Mode = ActionLoop.BotMode.Combat; modeSet = true;
                opt.Death = ActionLoop.DeathMethod.Suicide; deathSet = true;
                opt.Respawn = true; opt.MaxLives = 0;
                opt.TimeoutMs = 600_000; timeoutSet = true;
                break;
            case "mixed": // weighted wander/combat mix with deaths and respawns
                count = 12; concurrency = 12; opt.Mode = ActionLoop.BotMode.Mixed; modeSet = true;
                opt.Death = ActionLoop.DeathMethod.Suicide; deathSet = true;
                opt.Respawn = true;
                opt.TimeoutMs = 600_000; timeoutSet = true;
                break;
            case "bench": // ramped steady-wander cohort with a warm-up + window
                count = 16; concurrency = 16; opt.ActionCount = 0;
                joinRampMs = 15_000;
                benchWarmupMs = 30_000; benchWindowMs = 60_000;
                // ramp + warm-up + window + teardown margin; --timeout overrides.
                opt.TimeoutMs = 130_000; timeoutSet = true;
                // A pure join/action bench must not include telnet world pressure.
                spawnZombies = false; killFallback = false;
                break;
            case "":
                break;
            default:
                Console.Error.WriteLine(
                    $"FAIL: unknown --profile '{profile}' (probe|join-burst|steady-wander|death-soak|mixed|bench)");
                return 3;
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--host" && i + 1 < args.Length) opt.Host = args[++i];
            else if (args[i] == "--port" && i + 1 < args.Length) opt.Port = int.Parse(args[++i]);
            else if (args[i] == "--key" && i + 1 < args.Length) opt.Password = args[++i];
            else if (args[i] == "--password" && i + 1 < args.Length) opt.Password = args[++i];
            else if (args[i] == "--timeout" && i + 1 < args.Length)
            {
                opt.TimeoutMs = int.Parse(args[++i]);
                timeoutSet = true;
            }
            else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
            else if (args[i] == "--stats-json" && i + 1 < args.Length) statsJsonPath = args[++i];
            else if (args[i] == "--events-jsonl" && i + 1 < args.Length) eventsJsonlPath = args[++i];
            else if (args[i] == "--observe-cvar" && i + 1 < args.Length) observedCvars.Add(args[++i]);
            else if (args[i] == "--observe-buff" && i + 1 < args.Length) observedBuffs.Add(args[++i]);
            else if (args[i] == "--run-manifest" && i + 1 < args.Length) runManifestPath = args[++i];
            else if (args[i] == "--scenario-id" && i + 1 < args.Length) scenarioId = args[++i];
            else if (args[i] == "--ramp-ms" && i + 1 < args.Length)
                // Clamp: i*joinRampMs must not overflow the Task.Delay cast at scale.
                joinRampMs = Math.Clamp(int.Parse(args[++i]), 0, 3_600_000);
            else if (args[i] == "--id" && i + 1 < args.Length) opt.ClientId = int.Parse(args[++i]);
            else if (args[i] == "--name" && i + 1 < args.Length) opt.PlayerName = args[++i];
            else if (args[i] == "--actions" && i + 1 < args.Length) opt.ActionCount = int.Parse(args[++i]);
            else if (args[i] == "--seed" && i + 1 < args.Length) opt.ActionSeed = int.Parse(args[++i]);
            else if (args[i] == "--count" && i + 1 < args.Length) count = int.Parse(args[++i]);
            else if (args[i] == "--concurrency" && i + 1 < args.Length) concurrency = int.Parse(args[++i]);
            else if (args[i] == "--min-pass-rate" && i + 1 < args.Length) minPassRate = double.Parse(args[++i]);
            else if (args[i] == "--no-actions") opt.SkipActions = true;
            else if (args[i] == "--mixed-actions")
            {
                opt.WanderUntilDeath = false;
                opt.Mode = ActionLoop.BotMode.Mixed;
                modeSet = true;
            }
            else if (args[i] == "--max-dynamite" && i + 1 < args.Length)
                opt.MaxDynamitePerLife = int.Parse(args[++i]);
            else if ((args[i] == "--mode" || args[i] == "--bot-mode") && i + 1 < args.Length)
            {
                if (ActionLoop.TryParseMode(args[++i], out var mode))
                {
                    if (mode == ActionLoop.BotMode.Demolition
                        && opt.MaxDynamitePerLife == ActionLoop.DefaultMaxDynamitePerLife)
                        opt.MaxDynamitePerLife = ActionLoop.DemolitionMaxDynamitePerLife;
                    opt.Mode = mode;
                    opt.WanderUntilDeath = mode == ActionLoop.BotMode.Wander;
                    modeSet = true;
                }
            }
            else if (args[i] == "--bot-mix" && i + 1 < args.Length)
            {
                foreach (var part in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && ActionLoop.TryParseMode(kv[0].Trim(), out var m)
                        && int.TryParse(kv[1].Trim(), out var w) && w > 0)
                        botMix.Add((m, w));
                }
                if (botMix.Count > 0) modeSet = true;
            }
            else if (args[i] == "--death" && i + 1 < args.Length)
            {
                if (ActionLoop.TryParseDeath(args[++i], out var death))
                {
                    opt.Death = death;
                    deathSet = true;
                }
            }
            else if (args[i] == "--pace-ms" && i + 1 < args.Length)
                opt.PaceMs = int.Parse(args[++i]);
            else if (args[i] == "--spawn-zombies") spawnZombies = true;
            else if (args[i] == "--no-spawn-zombies") spawnZombies = false;
            else if (args[i] == "--no-kill-fallback") killFallback = false;
            else if (args[i] == "--kill-fallback") killFallback = true;
            else if (args[i] == "--telnet-port" && i + 1 < args.Length) telnetPort = int.Parse(args[++i]);
            else if (args[i] == "--telnet-password" && i + 1 < args.Length) telnetPassword = args[++i];
            else if (args[i] == "--telnet-host" && i + 1 < args.Length) telnetHost = args[++i];
            else if (args[i] == "--horde-every-ms" && i + 1 < args.Length) hordeEveryMs = int.Parse(args[++i]);
            else if (args[i] == "--horde-waves" && i + 1 < args.Length) hordeWaves = int.Parse(args[++i]);
            else if (args[i] == "--spawn-every-ms" && i + 1 < args.Length) spawnEveryMs = int.Parse(args[++i]);
            else if (args[i] == "--spawn-per-player" && i + 1 < args.Length) spawnPerPlayer = int.Parse(args[++i]);
            else if (args[i] == "--spawn-entity" && i + 1 < args.Length) spawnEntity = args[++i];
            else if (args[i] == "--no-respawn") opt.Respawn = false;
            else if (args[i] == "--respawn") opt.Respawn = true;
            else if (args[i] == "--max-lives" && i + 1 < args.Length) opt.MaxLives = int.Parse(args[++i]);
            else if (args[i] == "--respawn-delay-ms" && i + 1 < args.Length) opt.RespawnDelayMs = int.Parse(args[++i]);
            else if (args[i] == "--respawn-timeout-ms" && i + 1 < args.Length) opt.RespawnTimeoutMs = int.Parse(args[++i]);
            else if (args[i] == "--bench-warmup-ms" && i + 1 < args.Length) benchWarmupMs = int.Parse(args[++i]);
            else if (args[i] == "--bench-window-ms" && i + 1 < args.Length) benchWindowMs = int.Parse(args[++i]);
        }

        if (count < 1) count = 1;
        // Startup config gate: reject values that would silently misbehave
        // mid-run (unroutable port, gate that always fails/passes, instant
        // timeout, negative sleeps) with a named option and its valid range.
        if (!IsValidPort(opt.Port))
            return InvalidArg("--port", opt.Port.ToString(), "an integer 1..65535");
        if ((observedCvars.Count > 0 || observedBuffs.Count > 0) && string.IsNullOrWhiteSpace(eventsJsonlPath))
            return InvalidArg("--events-jsonl", "missing", "a path when --observe-cvar or --observe-buff is used");
        if (observedCvars.Any(string.IsNullOrWhiteSpace) || observedBuffs.Any(string.IsNullOrWhiteSpace))
            return InvalidArg("--observe-cvar/--observe-buff", "empty", "a non-empty exact state name");

        // Fail fast on an unwritable events sink: an unhandled constructor throw
        // here surfaced as a stack-trace crash instead of a clean usage error
        // naming the flag (same startup-gate contract as --port/--timeout).
        JsonLineEventWriter? eventWriter = null;
        if (!string.IsNullOrWhiteSpace(eventsJsonlPath))
        {
            try { eventWriter = new JsonLineEventWriter(eventsJsonlPath); }
            catch (Exception ex)
            {
                return InvalidArg("--events-jsonl", eventsJsonlPath,
                    $"a writable path ({ex.GetType().Name}: {ex.Message})");
            }
        }
        using var eventWriterOwned = eventWriter;
        if (!IsValidPort(telnetPort))
            return InvalidArg("--telnet-port", telnetPort.ToString(), "an integer 1..65535");
        if (!IsValidMinPassRate(minPassRate))
            return InvalidArg("--min-pass-rate", minPassRate.ToString(), "a fraction between 0 and 1");
        if (opt.TimeoutMs <= 0)
            return InvalidArg("--timeout", opt.TimeoutMs.ToString(), "a positive millisecond value");
        if (opt.RespawnDelayMs < 0)
            return InvalidArg("--respawn-delay-ms", opt.RespawnDelayMs.ToString(), ">= 0");
        if (opt.RespawnTimeoutMs <= 0)
            return InvalidArg("--respawn-timeout-ms", opt.RespawnTimeoutMs.ToString(), "a positive millisecond value");
        // Join bots are long-lived players and never free their slot, so
        // concurrency is the live-player cap. Default it to count (every bot a
        // simultaneous player; --ramp-ms staggers the joins). Warn loudly if the
        // caller pins it below count - that silently limits how many players
        // ever connect (this footgun stalled a 1000-player run at 64).
        if (concurrency <= 0)
            concurrency = count;
        else if (concurrency < count)
            Console.WriteLine(
                $"[{DateTime.UtcNow:O}] WARN --concurrency {concurrency} < --count {count}: only "
                + $"{concurrency} bots will be live at once; long-lived join bots never free slots. "
                + $"Use --concurrency {count} (or omit it) for {count} simultaneous players.");

        // Default: wander endlessly until zombies/rad/water/server kill the bot (no client self-kill).
        if (!modeSet)
        {
            opt.Mode = ActionLoop.BotMode.Wander;
            opt.WanderUntilDeath = true;
        }
        if (!deathSet)
            opt.Death = ActionLoop.DeathMethod.None;

        // Wall-clock budget: long for endless world-death walks; short estimate when --actions N set.
        if (!timeoutSet)
        {
            if (opt.ActionCount <= 0)
            {
                // Endless until world death — default 1 hour.
                opt.TimeoutMs = Math.Max(opt.TimeoutMs, 3_600_000);
            }
            else
            {
                int pace = opt.PaceMs > 0 ? opt.PaceMs : 50;
                int estimate = 30_000 + opt.ActionCount * pace + 10_000;
                if (count >= 50) estimate += 30_000;
                opt.TimeoutMs = Math.Max(opt.TimeoutMs, Math.Min(estimate, 3_600_000));
            }
        }

        opt.CohortSize = count;

        using var spawnCts = new CancellationTokenSource();
        Task? spawnTask = null;
        if (spawnZombies)
        {
            Console.WriteLine(
                $"[{DateTime.UtcNow:O}] ZOMBIE_SPAWN telnet={telnetHost}:{telnetPort} " +
                $"everyMs={spawnEveryMs} perPlayer={spawnPerPlayer} entity={spawnEntity}");
            // First wave after bots have had a chance to join.
            spawnTask = RunTelnetPressureLoop("spawn", spawnCts.Token,
                startDelayMs: 8_000, intervalMs: Math.Max(5_000, spawnEveryMs),
                errorBackoffMs: 10_000,
                () => new TelnetAdmin(telnetHost, telnetPort, telnetPassword, Console.WriteLine)
                {
                    KillFallback = killFallback,
                },
                admin => admin.SpawnZombiesNearPlayers(spawnEntity, spawnPerPlayer));
        }

        // Deterministic per-client mode from the weighted mix (repeatable across
        // runs). Interleaves buckets so adjacent client ids get different modes.
        int mixTotal = 0;
        foreach (var (_, w) in botMix) mixTotal += w;
        ActionLoop.BotMode ModeForClient(int clientId)
        {
            if (botMix.Count == 0 || mixTotal <= 0) return opt.Mode;
            // Spread the cohort proportionally across the weight space so the mix
            // holds at any cohort size (not just multiples of the weight total).
            int index = clientId - opt.ClientId;
            int slot = count > 0 ? (int)((long)index * mixTotal / count) % mixTotal : index % mixTotal;
            foreach (var (mode, weight) in botMix)
            {
                if (slot < weight) return mode;
                slot -= weight;
            }
            return botMix[^1].mode;
        }

        // Wandering-horde stream: periodic scout-horde bursts, independent of the
        // steady per-player trickle above. Off unless --horde-every-ms > 0.
        Task? hordeTask = null;
        if (hordeEveryMs > 0)
        {
            Console.WriteLine(
                $"[{DateTime.UtcNow:O}] WANDERING_HORDE telnet={telnetHost}:{telnetPort} "
                + $"everyMs={hordeEveryMs} waves={hordeWaves}");
            hordeTask = RunTelnetPressureLoop("horde", spawnCts.Token,
                startDelayMs: 20_000, intervalMs: Math.Max(15_000, hordeEveryMs),
                errorBackoffMs: 15_000,
                () => new TelnetAdmin(telnetHost, telnetPort, telnetPassword, Console.WriteLine),
                admin => admin.SpawnWanderingHorde(hordeWaves, 2));
        }

        // Per-bot session: rejoin on early disconnect until overall wall clock expires.
        var bench = benchWindowMs > 0 ? new BenchClock(benchWarmupMs, benchWindowMs) : null;
        (int rc, JoinStateMachine s) RunWithRejoin(int clientId, Action<string>? log)
        {
            var sessionSw = Stopwatch.StartNew();
            // Aggregate counters across rejoin attempts; the last attempt's state
            // snapshot still carries stage/death/entity fields for reporting.
            var totals = new JoinStateMachine();
            int lastRc = 1;
            JoinStateMachine last = new();
            int attempt = 0;
            var clientMode = ModeForClient(clientId);
            var stateObserver = eventWriter == null ? null : new NetworkStateObserver(
                clientId, observedCvars, observedBuffs, eventWriter.Write);
            int clientDynamite =
                clientMode == ActionLoop.BotMode.Demolition
                    && opt.MaxDynamitePerLife <= ActionLoop.DefaultMaxDynamitePerLife
                        ? ActionLoop.DemolitionMaxDynamitePerLife
                        : opt.MaxDynamitePerLife;
            // ShutdownRequested: DisconnectAllActive owns every live manager and
            // is sweeping; starting another join session would register a fresh
            // NetManager mid-teardown and race it with its own bot thread.
            while (sessionSw.ElapsedMilliseconds + 5_000 < opt.TimeoutMs && !GameJoinClient.ShutdownRequested)
            {
                attempt++;
                int remaining = (int)Math.Max(5_000, opt.TimeoutMs - sessionSw.ElapsedMilliseconds);
                var o = new GameJoinClient.Options
                {
                    Host = opt.Host,
                    Port = opt.Port,
                    Password = opt.Password,
                    PlayerName = opt.PlayerName,
                    TimeoutMs = remaining,
                    ActionCount = opt.ActionCount,
                    ActionSeed = opt.ActionSeed,
                    ClientId = clientId,
                    SkipActions = opt.SkipActions,
                    // GameJoinClient converts Wander+!WanderUntilDeath to Mixed;
                    // Mode is always clientMode here, so pass the flag through.
                    WanderUntilDeath = opt.WanderUntilDeath,
                    Mode = clientMode,
                    MaxDynamitePerLife = clientDynamite,
                    Death = opt.Death,
                    PaceMs = opt.PaceMs,
                    CohortSize = count,
                    Respawn = opt.Respawn,
                    MaxLives = opt.MaxLives,
                    RespawnDelayMs = opt.RespawnDelayMs,
                    RespawnTimeoutMs = opt.RespawnTimeoutMs,
                    LocalBindIp = GameJoinClient.LoopbackBindForIndex(clientId + attempt * 17),
                    Bench = bench,
                    OnLifeStarted = entityId =>
                    {
                        try
                        {
                            using var admin = new TelnetAdmin(telnetHost, telnetPort, telnetPassword, log);
                            if (admin.Connect())
                            {
                                string response = admin.Exec($"give {entityId} thrownDynamite 3");
                                log?.Invoke($"[{DateTime.UtcNow:O}] DYNAMITE_GIVE entity={entityId} count=3 response={response.Trim()}");
                            }
                            else
                            {
                                // Connect() already logged when a bot log exists; most
                                // cohort members have none, so route to stderr like the
                                // catch below or the missing dynamite load is invisible.
                                (log ?? Console.Error.WriteLine)(
                                    $"[{DateTime.UtcNow:O}] DYNAMITE_GIVE entity={entityId} telnet connect failed {telnetHost}:{telnetPort}");
                            }
                        }
                        catch (Exception ex)
                        {
                            (log ?? Console.Error.WriteLine)(
                                $"[{DateTime.UtcNow:O}] DYNAMITE_GIVE entity={entityId} failed={ex.GetType().Name}: {ex.Message}");
                        }
                    },
                    Log = log,
                    StateObserver = stateObserver,
                };
                var c = new GameJoinClient();
                try
                {
                    lastRc = c.Run(o);
                }
                catch (Exception ex)
                {
                    lastRc = 1;
                    // log is null for most cohort members; route to stderr so the
                    // fault is never invisible (summary only carries the count).
                    (log ?? Console.Error.WriteLine)(
                        $"[{DateTime.UtcNow:O}] join#{clientId} EX: {ex.GetType().Name}: {ex.Message}");
                }
                last = c.State;
                totals.AddCounters(last);
                if (attempt > 1) totals.RejoinCount++; // every retry past the first is a rejoin

                // Intentional end of budget (walked until timeout) or hard fail without join.
                // Recompute remaining fresh: a join attempt can burn most of the
                // budget, so the pre-attempt value would let the loop overshoot.
                string cause = last.DeathCause ?? "none";
                long remainMs = opt.TimeoutMs - sessionSw.ElapsedMilliseconds;
                if (cause is "timeout_alive" || remainMs < 15_000)
                    break;
                // Backoff + deterministic per-client jitter so 1000 bots that fail
                // together do not retry in unison (thundering herd on the server).
                // clientId-based jitter keeps runs reproducible (no RNG).
                int backoff(int baseMs, int step) =>
                    (int)Math.Min(15_000, baseMs + attempt * step + clientId % 500);
                if (last.EverJoined && (last.Stage == JoinStage.Disconnected || cause is "server_disconnect"))
                {
                    log?.Invoke(
                        $"[{DateTime.UtcNow:O}] REJOIN client={clientId} attempt={attempt} " +
                        $"cause={cause} remainingMs={remainMs}");
                    Thread.Sleep(backoff(2_000, 500));
                    continue;
                }
                if (!last.EverJoined)
                {
                    log?.Invoke(
                        $"[{DateTime.UtcNow:O}] REJOIN client={clientId} attempt={attempt} " +
                        $"no_join stage={last.Stage} remainingMs={remainMs}");
                    Thread.Sleep(backoff(3_000, 750));
                    continue;
                }
                break;
            }
            // Fold aggregate counters into the last state snapshot for reporting.
            last.SetCounters(totals);
            return (lastRc, last);
        }

        // Shared stats-json body for single- and multi-bot runs so the schema
        // cannot drift between the two writers (ping evidence included in both).
        Dictionary<string, object?> BuildStatsPayload(int total, int pass, in CohortCounters c)
        {
            double rate = total == 0 ? 0 : (double)pass / total;
            var ping = PingStats.Summary();
            return new Dictionary<string, object?>
            {
                ["schema"] = "7dtd.loadgen.stats.v1",
                ["scenarioId"] = string.IsNullOrEmpty(scenarioId) ? null : scenarioId,
                ["host"] = opt.Host,
                ["port"] = opt.Port,
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["total"] = total,
                ["pass"] = pass,
                ["fail"] = total - pass,
                ["passRate"] = rate,
                ["mode"] = opt.Mode.ToString(),
                ["death"] = opt.Death.ToString(),
                ["walks"] = c.Walks,
                ["jumps"] = c.Jumps,
                ["crouches"] = c.Crouches,
                ["aims"] = c.Aims,
                ["turns"] = c.Turns,
                ["strafes"] = c.Strafes,
                ["looks"] = c.Looks,
                ["chats"] = c.Chats,
                ["breaks"] = c.Breaks,
                ["attacks"] = c.Attacks,
                ["drowns"] = c.Drowns,
                ["suicides"] = c.Suicides,
                ["killed"] = c.Killed,
                ["diedClients"] = c.DiedClients,
                ["totalDeaths"] = c.TotalDeaths,
                ["totalRespawns"] = c.TotalRespawns,
                ["totalRejoins"] = c.TotalRejoins,
                ["minPassRate"] = minPassRate,
                ["gatePass"] = JoinGatePass(pass, total, minPassRate),
                ["pingSamples"] = ping.count,
                ["pingAvgMs"] = Math.Round(ping.avg, 1),
                ["pingP50Ms"] = ping.p50,
                ["pingP95Ms"] = ping.p95,
                ["pingMaxMs"] = ping.max,
                ["pingSpikesOver150Ms"] = ping.spikes,
            };
        }

        if (count == 1)
        {
            var lines = new List<string>();
            Action<string> log = s => { Console.WriteLine(s); lines.Add(s); };
            var (rc, sm) = RunWithRejoin(opt.ClientId, log);
            spawnCts.Cancel();
            AwaitTeardown("zombie_spawn", spawnTask);
            AwaitTeardown("wandering_horde", hordeTask);
            if (!string.IsNullOrEmpty(logPath))
                WriteArtifact("log", logPath, () => File.WriteAllLines(logPath, lines));
            // Single-bot runs still write stats-json so the bench lane evidence
            // is uniform (probe-15s/join-fast/join-probe/horde-lite are count=1).
            if (!string.IsNullOrEmpty(statsJsonPath))
            {
                var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                var payload = BuildStatsPayload(1, rc == 0 ? 1 : 0, CohortCounters.FromState(sm));
                WriteArtifact("stats", statsJsonPath, () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statsJsonPath))!);
                    File.WriteAllText(statsJsonPath,
                        System.Text.Json.JsonSerializer.Serialize(payload, jsonOpts) + "\n");
                });
            }
            return rc;
        }

        // Each bot's RunWithRejoin is synchronous and blocks its pool thread for the
        // whole session (Thread.Sleep pacing), so every live bot permanently pins one
        // ThreadPool thread. The pool only injects ~1-2 threads/sec above ProcessorCount,
        // so without this the ramp takes minutes to reach concurrency and the tool
        // silently under-loads the server. Provision the threads up front so --ramp-ms
        // is the real gate. (Caveat: ~N provisioned OS threads cost ~1 MB stack each;
        // the proper long-term fix is an async rewrite - Task.Delay, not Thread.Sleep.)
        ThreadPool.GetMinThreads(out _, out int minIocp);
        ThreadPool.SetMinThreads(concurrency + 16, Math.Max(minIocp, concurrency + 16));

        // Multi join — unique 127.x.x.x binds + bounded concurrency (dedicated rate-limit is per IP)
        Console.WriteLine(
            $"[{DateTime.UtcNow:O}] JOIN_LOAD count={count} concurrency={concurrency} " +
            $"host={opt.Host}:{opt.Port} actions={opt.ActionCount} mode={opt.Mode} death={opt.Death} " +
            $"timeoutMs={opt.TimeoutMs} spawnZombies={spawnZombies} killFallback={killFallback} " +
            $"bind=127.x multi-ip");
        // killFallback only takes effect inside the telnet spawn loop, so the
        // pressure warning fires on spawnZombies alone.
        if (spawnZombies)
            Console.WriteLine(
                $"[{DateTime.UtcNow:O}] WARNING: server-side pressure active - " +
                "telnet zombie spawning" +
                (killFallback ? " and admin kill fallback" : "") +
                ". These modify the world and raise server load; use --no-spawn-zombies " +
                "and/or --no-kill-fallback for a pure join/action measurement.");
        var results = new System.Collections.Concurrent.ConcurrentBag<(
            int id, int rc, JoinStage stage, int walks, int jumps, int crouches, int aims, int turns,
            int strafes, int looks, int chats, int breaks, int attacks,
            int drowns, int suicides, int killed, bool died, string deathCause, int entityId, string mode,
            int deathCount, int respawnCount, int rejoinCount)>();
        var gate = new SemaphoreSlim(concurrency);
        // Bench clock: window-sliced counts + per-second active-cohort curve.
        var running = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        var benchCts = new CancellationTokenSource();
        Task? benchSampler = null;
        if (bench != null)
            benchSampler = Task.Run(async () =>
        {
            try
            {
                while (!benchCts.IsCancellationRequested)
                {
                    bench.SampleActive(running.Count);
                    await Task.Delay(1000, benchCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* normal stop */ }
        });
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            if (joinRampMs > 0 && count > 1)
                await Task.Delay(RampDelayMs(i, count, joinRampMs)).ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false);
            int id = opt.ClientId + i;
            running.TryAdd(id, 0);
            try
            {
                Action<string>? log = i < 3 ? Console.WriteLine : null;
                try
                {
                    var (rc, s) = RunWithRejoin(id, log);
                    results.Add((id, rc, s.Stage, s.WalkActions, s.JumpActions, s.CrouchActions, s.AimActions,
                        s.TurnActions, s.StrafeActions, s.LookActions, s.ChatActions, s.BreakBlockActions,
                        s.AttackActions, s.DrownActions, s.SuicideActions, s.KilledActions, s.Died,
                        s.DeathCause, s.EntityId, s.BotModeName, s.DeathCount, s.RespawnCount,
                        s.RejoinCount));
                }
                catch (Exception ex)
                {
                    // Unconditional: a cohort-wide fault must never be invisible
                    // just because the bot's console log was throttled off.
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:O}] join#{id} EX: {ex.GetType().Name}: {ex.Message}");
                    results.Add((id, 1, JoinStage.Failed, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, "exception", -1, opt.Mode.ToString(), 0, 0, 0));
                }
            }
            finally { running.TryRemove(id, out _); gate.Release(); }
        })).ToArray();
        Task.WaitAll(tasks);
        benchCts.Cancel();
        AwaitTeardown("bench_sampler", benchSampler);
        bench?.SampleActive(0); // final sample so the curve shows the ramp-down
        spawnCts.Cancel();
        AwaitTeardown("zombie_spawn", spawnTask);
        AwaitTeardown("wandering_horde", hordeTask);

        int pass = results.Count(r => r.rc == 0);
        // Cast to long before summing: per-bot counts are int, but the total
        // across up to 1000 bots on a multi-day run exceeds int.MaxValue and
        // Sum(int) wraps silently (unchecked) to a negative total.
        long walks = results.Sum(r => (long)r.walks);
        long jumps = results.Sum(r => (long)r.jumps);
        long crouches = results.Sum(r => (long)r.crouches);
        long aims = results.Sum(r => (long)r.aims);
        long turns = results.Sum(r => (long)r.turns);
        long strafes = results.Sum(r => (long)r.strafes);
        long looks = results.Sum(r => (long)r.looks);
        long chats = results.Sum(r => (long)r.chats);
        long breaks = results.Sum(r => (long)r.breaks);
        long attacks = results.Sum(r => (long)r.attacks);
        long drowns = results.Sum(r => (long)r.drowns);
        long suicides = results.Sum(r => (long)r.suicides);
        long killed = results.Sum(r => (long)r.killed);
        int died = results.Count(r => r.died);
        double rate = count == 0 ? 0 : (double)pass / count;

        var byCause = results
            .GroupBy(r => string.IsNullOrEmpty(r.deathCause) ? "none" : r.deathCause)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}={g.Count()}")
            .ToList();
        int worldKilled = results.Count(r => r.deathCause is "world_killed" or "world_death");
        int worldDrown = results.Count(r => r.deathCause is "world_drown");
        int worldRad = results.Count(r => r.deathCause is "world_radiation");
        int timedOut = results.Count(r => r.deathCause is "timeout_alive");
        int disc = results.Count(r => r.deathCause is "server_disconnect");
        int selfKill = results.Count(r => r.deathCause is "drown_fatal" or "suicide" or "suicide_fallback" or "killed_external");
        int diedEx = results.Count(r => r.deathCause == "exception");
        int totalDeaths = results.Sum(r => r.deathCount);
        int totalRespawns = results.Sum(r => r.respawnCount);
        int totalRejoins = results.Sum(r => r.rejoinCount);

        var report =
            $"JOIN_SUMMARY total={count} pass={pass} fail={count - pass} passRate={rate:P2} mode={opt.Mode} death={opt.Death} respawn={opt.Respawn}\n" +
            $"JOIN_ACTIONS walks={walks} jumps={jumps} crouch={crouches} aim={aims} turn={turns} " +
            $"strafe={strafes} look={looks} chat={chats} break={breaks} attack={attacks} " +
            $"diedClients={died} totalDeaths={totalDeaths} totalRespawns={totalRespawns} " +
            $"totalRejoins={totalRejoins}\n" +
            $"DEATH_STATS total={count} died={died} alive={count - died} " +
            $"world_killed={worldKilled} world_drown={worldDrown} world_radiation={worldRad} " +
            $"timeout_alive={timedOut} disconnect={disc} self_kill={selfKill} exception={diedEx}\n" +
            $"DEATH_HISTOGRAM {string.Join(" ", byCause)}\n" +
            string.Join("\n", results.OrderBy(r => r.id).Take(30).Select(r =>
                $"  id={r.id} rc={r.rc} mode={r.mode} entity={r.entityId} w={r.walks} j={r.jumps} " +
                $"deaths={r.deathCount} respawns={r.respawnCount} rejoins={r.rejoinCount} " +
                $"lastDied={r.died} cause={r.deathCause}"));
        if (bench is { } b)
        {
            var (wStart, wEnd) = b.WindowBounds;
            var (wActions, wDeaths, wRespawns) = b.WindowCounts;
            double aps = b.WindowMs > 0 ? wActions * 1000.0 / b.WindowMs : 0;
            double jps = wStart > 0 ? pass * 1000.0 / wStart : 0;
            Console.WriteLine(
                $"BENCH_SUMMARY warmupMs={b.WarmupMs} windowMs={b.WindowMs} " +
                $"actionsInWindow={wActions} actionsPerSec={aps:0.00} " +
                $"deathsInWindow={wDeaths} respawnsInWindow={wRespawns} " +
                $"joinRatePerSec={jps:0.000} activeMin={b.ActiveMin} activeMax={b.ActiveMax} " +
                $"activeAtWindowStart={b.ActiveAtWindowStart} activeAtWindowEnd={b.ActiveAtWindowEnd}");
        }
        Console.WriteLine(report);
        if (!string.IsNullOrEmpty(statsJsonPath) || !string.IsNullOrEmpty(runManifestPath))
        {
            var payload = BuildStatsPayload(count, pass, new CohortCounters(
                walks, jumps, crouches, aims, turns, strafes, looks, chats, breaks, attacks,
                drowns, suicides, killed, died, totalDeaths, totalRespawns, totalRejoins));
            payload["world_killed"] = worldKilled;
            payload["timeout_alive"] = timedOut;
            payload["disconnect"] = disc;
            if (bench is { } b2)
            {
                var (wStart, wEnd) = b2.WindowBounds;
                var (wActions, wDeaths, wRespawns) = b2.WindowCounts;
                payload["bench"] = new Dictionary<string, object?>
                {
                    ["warmupMs"] = b2.WarmupMs,
                    ["windowMs"] = b2.WindowMs,
                    ["windowStartMs"] = wStart,
                    ["windowEndMs"] = wEnd,
                    ["actionsInWindow"] = wActions,
                    ["actionsPerSec"] = Math.Round(b2.WindowMs > 0 ? wActions * 1000.0 / b2.WindowMs : 0, 2),
                    ["deathsInWindow"] = wDeaths,
                    ["respawnsInWindow"] = wRespawns,
                    ["joinRatePerSec"] = Math.Round(wStart > 0 ? pass * 1000.0 / wStart : 0, 3),
                    ["activeMin"] = b2.ActiveMin,
                    ["activeMax"] = b2.ActiveMax,
                    ["activeAtWindowStart"] = b2.ActiveAtWindowStart,
                    ["activeAtWindowEnd"] = b2.ActiveAtWindowEnd,
                    ["activeCurve"] = b2.ActiveCurve().Select(s => new[] { s.Ms, s.Active }).ToList(),
                };
            }
            var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            if (!string.IsNullOrEmpty(statsJsonPath))
            {
                WriteArtifact("stats", statsJsonPath, () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statsJsonPath))!);
                    File.WriteAllText(statsJsonPath, System.Text.Json.JsonSerializer.Serialize(payload, jsonOpts) + "\n");
                });
            }
            if (!string.IsNullOrEmpty(runManifestPath))
            {
                var clients = results.OrderBy(r => r.id).Select(r => new Dictionary<string, object?>
                {
                    ["id"] = r.id,
                    ["rc"] = r.rc,
                    ["mode"] = r.mode,
                    ["entityId"] = r.entityId,
                    ["walks"] = r.walks,
                    ["deaths"] = r.deathCount,
                    ["respawns"] = r.respawnCount,
                    ["died"] = r.died,
                    ["deathCause"] = r.deathCause,
                }).ToList();
                var run = new Dictionary<string, object?>
                {
                    ["schema"] = "7dtd.loadgen.run.v1",
                    ["kind"] = "join",
                    ["scenarioId"] = string.IsNullOrEmpty(scenarioId) ? null : scenarioId,
                    ["cohort"] = payload,
                    ["clients"] = clients,
                    ["product"] = new Dictionary<string, object?>
                    {
                        ["name"] = "RealEarth",
                        ["priorityFocus"] = "P0-P1",
                        ["notes"] = "Tall Y + inject soak when dedicated expanded",
                    },
                };
                WriteArtifact("run_manifest", runManifestPath, () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(runManifestPath))!);
                    File.WriteAllText(runManifestPath, System.Text.Json.JsonSerializer.Serialize(run, jsonOpts) + "\n");
                });
            }
        }
        if (!string.IsNullOrEmpty(logPath))
        {
            WriteArtifact("log", logPath, () => File.WriteAllText(logPath, report + "\n"));
            var csvPath = Path.ChangeExtension(logPath, null) + "_deaths.csv";
            var csv = new System.Text.StringBuilder();
            csv.AppendLine(
                "id,rc,mode,stage,entityId,walks,jumps,crouches,aims,turns,strafes,looks,chats," +
                "breaks,attacks,drowns,suicides,killed,died,deathCause,deathCount,respawnCount,rejoinCount");
            foreach (var r in results.OrderBy(x => x.id))
            {
                csv.AppendLine(
                    $"{r.id},{r.rc},{r.mode},{r.stage},{r.entityId},{r.walks},{r.jumps}," +
                    $"{r.crouches},{r.aims},{r.turns},{r.strafes},{r.looks},{r.chats}," +
                    $"{r.breaks},{r.attacks},{r.drowns},{r.suicides},{r.killed},{r.died}," +
                    $"{r.deathCause},{r.deathCount},{r.respawnCount},{r.rejoinCount}");
            }
            WriteArtifact("DEATH_CSV", csvPath, () => File.WriteAllText(csvPath, csv.ToString()));
        }
        return JoinGatePass(pass, count, minPassRate) ? 0 : 1;
    }

    static int RunProbe(string[] args)
    {
        string host = "127.0.0.1";
        // Bots (probe included) speak LiteNetLib and must target the data port =
        // ServerPort + 2 (26902 for the stock 26900 server); a probe on the game
        // client port gets no protocol response and fails.
        int port = 26902;
        string key = Environment.GetEnvironmentVariable("LOADGEN_KEY") is { Length: > 0 } k ? k : "";
        int timeoutMs = 8000;
        string? logPath = null;
        int clientId = 1;
        int count = 1;
        int concurrency = 0;
        double minPassRate = 0.95;
        bool quiet = false;
        int rampMs = 0;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--host" && i + 1 < args.Length) host = args[++i];
            else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else if (args[i] == "--key" && i + 1 < args.Length) key = args[++i];
            else if (args[i] == "--timeout" && i + 1 < args.Length) timeoutMs = int.Parse(args[++i]);
            else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
            else if (args[i] == "--id" && i + 1 < args.Length) clientId = int.Parse(args[++i]);
            else if (args[i] == "--count" && i + 1 < args.Length) count = int.Parse(args[++i]);
            else if (args[i] == "--concurrency" && i + 1 < args.Length) concurrency = int.Parse(args[++i]);
            else if (args[i] == "--min-pass-rate" && i + 1 < args.Length) minPassRate = double.Parse(args[++i]);
            // Clamp like the join parser: the ramp delay cast must not overflow.
            else if (args[i] == "--ramp-ms" && i + 1 < args.Length) rampMs = Math.Clamp(int.Parse(args[++i]), 0, 3_600_000);
            else if (args[i] == "--quiet") quiet = true;
        }

        if (count < 1) count = 1;
        if (!IsValidPort(port))
            return InvalidArg("--port", port.ToString(), "an integer 1..65535");
        if (!IsValidMinPassRate(minPassRate))
            return InvalidArg("--min-pass-rate", minPassRate.ToString(), "a fraction between 0 and 1");
        if (timeoutMs <= 0)
            return InvalidArg("--timeout", timeoutMs.ToString(), "a positive millisecond value");
        if (count == 1)
        {
            Action<string>? log = quiet ? null : Console.WriteLine;
            var result = LiteNetProbe.Run(host, port, key, timeoutMs, clientId, log);
            if (!string.IsNullOrEmpty(logPath))
                WriteArtifact("log", logPath, () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
                    File.WriteAllLines(logPath, result.Lines);
                });
            if (!result.Pass)
            {
                Console.WriteLine($"[{DateTime.UtcNow:O}] [fake#{clientId}] FAIL: no LiteNetLib protocol progress");
                return 1;
            }
            Console.WriteLine($"[{DateTime.UtcNow:O}] [fake#{clientId}] PASS: protocol progress beyond socket open");
            return 0;
        }

        if (concurrency <= 0)
            concurrency = Math.Clamp(Environment.ProcessorCount * 32, 64, 512);
        concurrency = Math.Min(concurrency, count);
        Console.WriteLine(
            $"[{DateTime.UtcNow:O}] LOAD start host={host} port={port} count={count} " +
            $"concurrency={concurrency} timeoutMs={timeoutMs} minPassRate={minPassRate:P0}");
        var summary = LoadRunner.Run(host, port, key, timeoutMs, count, concurrency, rampMs, quiet, idBase: clientId);
        if (!string.IsNullOrEmpty(logPath))
            WriteArtifact("log", logPath, () => File.WriteAllText(logPath, summary.ToReport()));
        Console.WriteLine(summary.ToReport());
        if (!Program.JoinGatePass(summary.Pass, summary.Total, minPassRate))
        {
            Console.WriteLine($"FAIL: passRate={summary.PassRate:P2} < minPassRate={minPassRate:P2}");
            return 1;
        }
        Console.WriteLine($"PASS: load {summary.Total} clients passRate={summary.PassRate:P2}");
        return 0;
    }

    static void PrintHelp()
    {
        Console.WriteLine(
            "7dtd-loadgen — LiteNetLib probe + full join + bot actions\n" +
            "Modes:\n" +
            "  (default) probe     LiteNetLib connectivity only\n" +
            "  --join              Full join path + bot action loop\n" +
            "  --self-test         In-process LiteNetLib host+probe (scale with --count)\n" +
            "  --self-test-join    In-process mock 7DTD join + actions (CI gate)\n" +
            "\n" +
            "Join bot flags:\n" +
            "  --profile probe|join-burst|steady-wander|death-soak|mixed|bench\n" +
            "      preset cohort defaults; explicit flags override per key\n" +
            "      bench = ramped wander cohort + warm-up + measurement window\n" +
            "  --bench-warmup-ms N   bench warm-up before the window (default 30000)\n" +
            "  --bench-window-ms N   bench measurement window; >0 enables the bench\n" +
            "      summary (stats-json bench block + BENCH_SUMMARY line)\n" +
            "  --mode wander|mixed|chatty|combat|patrol|chaos|demolition|bait|kite|traverse\n" +
            "      default: wander (walk until world death)\n" +
            "  --death none|...   default none: never self-kill; wait for world death\n" +
            "  --actions N         live steps (0 or omit = endless until death/timeout)\n" +
            "  --respawn / --no-respawn   after death request spawn and walk again (default on)\n" +
            "  --max-lives N       stop after N deaths (0 = unlimited until --timeout)\n" +
            "  --respawn-delay-ms N  wait after death before respawn (default 1500)\n" +
            "  --respawn-timeout-ms N  max wait for server to confirm respawn (default 40000)\n" +
            "  --spawn-zombies     telnet-spawn zombies near bots (default on)\n" +
            "  --no-spawn-zombies  disable telnet spawns\n" +
            "  --telnet-host/port/password  dedicated telnet (default 127.0.0.1:8081 retest)\n" +
            "  --pace-ms N --seed N --name NAME --count N --concurrency N\n" +
            "  --host --port --timeout --log --min-pass-rate --no-actions\n" +
            "  --observe-cvar NAME  observe one exact replicated CVar (repeatable)\n" +
            "  --observe-buff NAME  observe one exact replicated buff (repeatable)\n" +
            "  --events-jsonl PATH  write filtered joined/state events as JSON lines\n" +
            "  --golden-wire       Assert package body layouts vs Assembly-CSharp IL sizes\n" +
            "  -V / --version      print client version and exit\n" +
            "Notes:\n" +
            "  Walk → world kill → DEATH → respawn → walk again until --timeout. No self-kill.\n" +
            "  Default timeout 1 hour. Rejoins on early disconnect. Telnet zed spawn for empty worlds.\n" +
            "Secrets via environment (flags override; avoids ps-visible argv):\n" +
            "  LOADGEN_KEY              server join password (--key/--password fallback)\n" +
            "  LOADGEN_TELNET_PASSWORD  admin telnet password (--telnet-password fallback)\n" +
            "Examples:\n" +
            "  7dtd-loadgen --join --host 127.0.0.1 --port 26902 --count 8\n" +
            "  7dtd-loadgen --join --count 4 --timeout 1800000 --max-lives 10\n" +
            "  7dtd-loadgen --self-test-join --actions 24\n" +
            "  7dtd-loadgen --golden-wire\n");
    }
}
