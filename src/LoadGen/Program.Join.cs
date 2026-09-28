using System.Diagnostics;

namespace SevenDTD.LoadGen;

// Everything join mode needs: the cohort run itself plus the workload blocks
// and artifact writers only it calls. AwaitTeardown lives in Program.Support.cs;
// the fault and artifact IO is RunReport's, shared with the non-CLI layers.
public static partial class Program
{
    static int RunJoin(string[] args)
    {
        var opt = new GameJoinClient.Options();
        // Credentials come from the environment only. There is no flag for
        // them: argv is world-readable in the process table, and a flag that
        // exists gets used. Passing one is rejected, never ignored.
        opt.Password = Environment.GetEnvironmentVariable("LOADGEN_KEY") is { Length: > 0 } envKey ? envKey : opt.Password;
        string? logPath = null;
        string? statsJsonPath = null;
        string? runManifestPath = null;
        string? eventsJsonlPath = null;
        var observedCvars = new List<string>();
        var observedBuffs = new List<string>();
        string scenarioId = Environment.GetEnvironmentVariable("LOADGEN_SCENARIO_ID") ?? "";
        // Console lines are the only record of a run until its artifacts land,
        // and the artifacts carry the scenario id. Echoing it on the run header
        // is what ties a wall of bot lines back to one stats json / manifest.
        string scenarioTag = string.IsNullOrEmpty(scenarioId) ? "" : $" scenario={scenarioId}";
        int joinRampMs = 0;
        int count = 1;
        int concurrency = 0;
        double minPassRate = 1.0;
        bool modeSet = false;
        bool timeoutSet = false;
        // Height-test worlds have empty prefabs → no natural zeds; telnet-spawn by default on join.
        bool spawnZombies = true;
        bool killFallback = true;
        string telnetHost = "127.0.0.1";
        int telnetPort = 8081;
        // Test-only lab default; override per environment via the env var
        // (AGENTS.md rule 4: prefer env / local config).
        string telnetPassword =
            Environment.GetEnvironmentVariable("LOADGEN_TELNET_PASSWORD") is { Length: > 0 } envPw
                ? envPw
                : "retest";
        int spawnEveryMs = 20_000;
        int spawnPerPlayer = 4;
        // Telnet wave cadence floors: a faster cadence is never run, and the
        // effective value is logged when a request lands under the floor.
        const int MinSpawnEveryMs = 5_000;
        const int MinHordeEveryMs = 15_000;
        string spawnEntity = "zombieBoe";
        // Benchmark mode: joins settle during warm-up, the measurement window is
        // [warmupMs, warmupMs+windowMs) after the cohort start, and the stats-json
        // gets a bench block (window counts + active-client curve). 0 = disabled.
        int benchWarmupMs = 30_000;
        int benchWindowMs = 0;
        // Suppress per-client progress on stdout; the summary, warnings and
        // every artifact path keep writing.
        bool quiet = false;
        // Wandering hordes: periodic scout-horde bursts that spawn at distance and
        // path in as a group. 0 = off. Slower cadence than the per-player trickle.
        int hordeEveryMs = 0;
        int hordeWaves = 3;
        // Weighted per-bot mode mix, e.g. "traverse:35,combat:20,bait:15". Empty
        // -> whole cohort uses opt.Mode. Assigned deterministically by client id
        // so the profile is repeatable.
        var botMix = new List<(ActionLoop.BotMode mode, int weight)>();
        // Explicit --max-dynamite must win over the Demolition auto-raise
        // regardless of flag order on the command line.
        bool maxDynamiteSet = false;

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
                break;
            case "join-burst": // many simultaneous joins, short steps, no death
                count = 24; concurrency = 24; opt.ActionCount = 10;
                opt.TimeoutMs = 120_000; timeoutSet = true;
                break;
            case "steady-wander": // endless wander for a soak window
                count = 8; concurrency = 8; opt.ActionCount = 0;
                opt.TimeoutMs = 900_000; timeoutSet = true;
                break;
            case "death-soak": // combat + self-kill + respawn loop
                count = 6; concurrency = 6; opt.ActionCount = 60;
                opt.Mode = ActionLoop.BotMode.Combat; modeSet = true;
                opt.Death = ActionLoop.DeathMethod.Suicide;
                opt.Respawn = true; opt.MaxLives = 0;
                opt.TimeoutMs = 600_000; timeoutSet = true;
                break;
            case "mixed": // weighted wander/combat mix with deaths and respawns
                count = 12; concurrency = 12; opt.Mode = ActionLoop.BotMode.Mixed; modeSet = true;
                opt.Death = ActionLoop.DeathMethod.Suicide;
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
                    $"FAIL: unknown --profile '{profile}' (probe|join-burst|steady-wander|death-soak|mixed|bench) (see --help)");
                return 2;
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--host" && i + 1 < args.Length) opt.Host = args[++i];
            else if (args[i] == "--port" && i + 1 < args.Length) opt.Port = int.Parse(args[++i]);
            else if (args[i] == "--timeout" && i + 1 < args.Length)
            {
                if (!TryParseTimeoutMs(args[++i], out int timeoutMs))
                    return InvalidArg("--timeout", args[i],
                        $"a positive millisecond value up to {MaxTimeoutMs} (~24.9 days)");
                opt.TimeoutMs = timeoutMs;
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
            {
                if (!TryParseRampMs(args[++i], out int rampMs))
                    return InvalidArg("--ramp-ms", args[i],
                        $"an integer 0..{MaxRampMs} (per-bot join stagger)");
                joinRampMs = rampMs;
            }
            else if (args[i] == "--id" && i + 1 < args.Length)
            {
                if (!TryParseClientId(args[++i], out int clientId))
                    return InvalidArg("--id", args[i], $"an integer 0..{MaxClientId}");
                opt.ClientId = clientId;
            }
            else if (args[i] == "--name" && i + 1 < args.Length) opt.PlayerName = args[++i];
            else if (args[i] == "--actions" && i + 1 < args.Length) opt.ActionCount = int.Parse(args[++i]);
            else if (args[i] == "--seed" && i + 1 < args.Length) opt.ActionSeed = int.Parse(args[++i]);
            else if (args[i] == "--count" && i + 1 < args.Length) count = int.Parse(args[++i]);
            else if (args[i] == "--concurrency" && i + 1 < args.Length) concurrency = int.Parse(args[++i]);
            else if (args[i] == "--min-pass-rate" && i + 1 < args.Length)
            {
                if (!TryParseMinPassRate(args[++i], out double parsedRate))
                    return InvalidArg("--min-pass-rate", args[i], "a fraction between 0 and 1");
                minPassRate = parsedRate;
            }
            else if (args[i] == "--no-actions") opt.SkipActions = true;
            else if (args[i] == "--max-dynamite" && i + 1 < args.Length)
            {
                opt.MaxDynamitePerLife = int.Parse(args[++i]);
                maxDynamiteSet = true;
            }
            else if ((args[i] == "--mode" || args[i] == "--bot-mode") && i + 1 < args.Length)
            {
                string flag = args[i];
                string raw = args[++i];
                // An unparsable mode used to fall through as wander, so a typo
                // silently ran a different workload than the script asked for.
                if (!ActionLoop.TryParseMode(raw, out var mode))
                    return InvalidArg(flag, raw, ModeList);
                opt.Mode = mode;
                modeSet = true;
            }
            else if (args[i] == "--bot-mix" && i + 1 < args.Length)
            {
                string raw = args[++i];
                foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && ActionLoop.TryParseMode(kv[0].Trim(), out var m)
                        && int.TryParse(kv[1].Trim(), out var w) && w > 0)
                        botMix.Add((m, w));
                }
                if (botMix.Count == 0)
                    return InvalidArg("--bot-mix", raw,
                        "mode:weight pairs with a positive weight, e.g. traverse:35,combat:20; modes: " + ModeList);
                modeSet = true;
            }
            else if (args[i] == "--death" && i + 1 < args.Length)
            {
                string raw = args[++i];
                if (!ActionLoop.TryParseDeath(raw, out var death))
                    return InvalidArg("--death", raw, DeathList);
                opt.Death = death;
            }
            else if (args[i] == "--pace-ms" && i + 1 < args.Length)
                opt.PaceMs = int.Parse(args[++i]);
            else if (args[i] == "--spawn-zombies") spawnZombies = true;
            else if (args[i] == "--no-spawn-zombies") spawnZombies = false;
            else if (args[i] == "--no-kill-fallback") killFallback = false;
            else if (args[i] == "--kill-fallback") killFallback = true;
            else if (args[i] == "--telnet-port" && i + 1 < args.Length) telnetPort = int.Parse(args[++i]);
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
            else if (args[i] == "--quiet") quiet = true;
        }

        // A 0 or negative cohort was raised to 1 in silence, so a typo in
        // LOADGEN_COUNT or --count produced a one-bot run whose summary and
        // stats json read downstream as a measured result. The cohort is the
        // headline number of a load run: reject it.
        if (!IsValidCount(count))
            return InvalidArg("--count", count.ToString(), "an integer 1 or more (bots in the cohort)");

        // Demolition's raised per-life cap applies only when the caller did not
        // pin one: an explicit --max-dynamite N bounds charges per life for
        // every mode ("demolition default 200, others 3"). The per-client mode
        // (cohort mode or a --bot-mix entry) decides who takes the raised default.
        int DynamiteCapFor(ActionLoop.BotMode m) =>
            !maxDynamiteSet && m == ActionLoop.BotMode.Demolition
                ? ActionLoop.DemolitionMaxDynamitePerLife
                : opt.MaxDynamitePerLife;

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
        // Every --spawn-entity entry is interpolated into a level-0 admin
        // command, so a value carrying extra arguments must be rejected here
        // rather than silently dropped at the console.
        string[] spawnEntityParts = spawnEntity.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (spawnEntityParts.Any(p => !TelnetAdmin.IsSafeCommandToken(p)))
            return InvalidArg("--spawn-entity", spawnEntity,
                "a comma-separated list matching [A-Za-z0-9._-]+ per entry");
        if (!IsValidMinPassRate(minPassRate))
            return InvalidArg("--min-pass-rate", minPassRate.ToString(), "a fraction between 0 and 1");
        if (opt.TimeoutMs <= 0)
            return InvalidArg("--timeout", opt.TimeoutMs.ToString(), "a positive millisecond value");
        if (opt.RespawnDelayMs < 0)
            return InvalidArg("--respawn-delay-ms", opt.RespawnDelayMs.ToString(), ">= 0");
        if (opt.RespawnTimeoutMs <= 0)
            return InvalidArg("--respawn-timeout-ms", opt.RespawnTimeoutMs.ToString(), "a positive millisecond value");
        // 0 is the documented "off"/"endless" sentinel for every knob below, so
        // only a negative value is a defect. Each one failed differently when
        // it slipped through: --bench-window-ms -1 made the bench block vanish
        // while the run still printed a summary, --spawn-per-player -4 built a
        // nonsense telnet console command, --actions -1 is indistinguishable
        // from "endless" downstream and reached stats json as actions: -1.
        if (opt.ActionCount < 0)
            return InvalidArg("--actions", opt.ActionCount.ToString(), "an action count >= 0 (0 = until the timeout)");
        if (benchWarmupMs < 0)
            return InvalidArg("--bench-warmup-ms", benchWarmupMs.ToString(), ">= 0");
        if (benchWindowMs < 0)
            return InvalidArg("--bench-window-ms", benchWindowMs.ToString(), ">= 0 (0 disables the bench block)");
        if (spawnEveryMs < 0)
            return InvalidArg("--spawn-every-ms", spawnEveryMs.ToString(), ">= 0 (0 disables the zombie trickle)");
        if (spawnPerPlayer < 0)
            return InvalidArg("--spawn-per-player", spawnPerPlayer.ToString(), ">= 0 (zombies per player per wave)");
        if (hordeEveryMs < 0)
            return InvalidArg("--horde-every-ms", hordeEveryMs.ToString(), ">= 0 (0 disables scout hordes)");
        if (hordeWaves < 0)
            return InvalidArg("--horde-waves", hordeWaves.ToString(), ">= 0 (waves per horde burst)");
        if (opt.MaxDynamitePerLife < 0)
            return InvalidArg("--max-dynamite", opt.MaxDynamitePerLife.ToString(), ">= 0 (0 denies dynamite)");
        if (opt.MaxLives < 0)
            return InvalidArg("--max-lives", opt.MaxLives.ToString(), ">= 0 (0 = respawn forever)");
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
        }

        // Wall-clock budget: long for endless world-death walks; short estimate when --actions N set.
        if (!timeoutSet)
        {
            if (opt.ActionCount <= 0)
            {
                // Endless until world death: default 1 hour.
                opt.TimeoutMs = Math.Max(opt.TimeoutMs, 3_600_000);
            }
            else
            {
                int pace = opt.PaceMs > 0 ? opt.PaceMs : 50;
                // Long math before the multiply: ActionCount * pace wraps int at
                // e.g. --actions 50000000 --pace-ms 60 (3e9 ms), and the wrapped
                // negative would silently shrink this run's wall-clock budget.
                long estimate = 30_000L + (long)opt.ActionCount * pace + 10_000;
                if (count >= 50) estimate += 30_000;
                opt.TimeoutMs = (int)Math.Max(opt.TimeoutMs, Math.Min(estimate, 3_600_000));
            }
        }

        using var spawnCts = new CancellationTokenSource();
        // One shared console for the whole cohort's per-life grants. The bot
        // enqueues and keeps walking: a synchronous telnet round trip inside
        // the life loop cost every bot its action loop once per life.
        using var provisioner = new TelnetProvisioner(
            () => new TelnetAdmin(telnetHost, telnetPort, telnetPassword, Console.WriteLine),
            Console.Error.WriteLine);
        Task? spawnTask = null;
        if (spawnZombies)
        {
            int spawnIntervalMs = Math.Max(MinSpawnEveryMs, spawnEveryMs);
            Console.WriteLine(
                $"[{DateTime.UtcNow:O}] ZOMBIE_SPAWN telnet={telnetHost}:{telnetPort} " +
                $"everyMs={spawnIntervalMs} perPlayer={spawnPerPlayer} entity={spawnEntity}");
            if (spawnIntervalMs != spawnEveryMs)
                Console.WriteLine(
                    $"[{DateTime.UtcNow:O}] WARN --spawn-every-ms {spawnEveryMs} raised to the " +
                    $"{MinSpawnEveryMs} ms floor (a faster wave cadence is not run)");
            // First wave after bots have had a chance to join.
            spawnTask = RunTelnetPressureLoop("spawn", spawnCts.Token,
                startDelayMs: 8_000, intervalMs: spawnIntervalMs,
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
            int hordeIntervalMs = Math.Max(MinHordeEveryMs, hordeEveryMs);
            Console.WriteLine(
                $"[{DateTime.UtcNow:O}] WANDERING_HORDE telnet={telnetHost}:{telnetPort} "
                + $"everyMs={hordeIntervalMs} waves={hordeWaves}");
            if (hordeIntervalMs != hordeEveryMs)
                Console.WriteLine(
                    $"[{DateTime.UtcNow:O}] WARN --horde-every-ms {hordeEveryMs} raised to the " +
                    $"{MinHordeEveryMs} ms floor (a faster horde cadence is not run)");
            // Each wave opens a fresh telnet session, so the rotation cursor
            // lives here rather than on the (per-wave) admin.
            int hordeCursor = 0;
            hordeTask = RunTelnetPressureLoop("horde", spawnCts.Token,
                startDelayMs: 20_000, intervalMs: hordeIntervalMs,
                errorBackoffMs: 15_000,
                () => new TelnetAdmin(telnetHost, telnetPort, telnetPassword, Console.WriteLine),
                admin => admin.SpawnWanderingHorde(hordeWaves, 2, ref hordeCursor));
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
            int clientDynamite = DynamiteCapFor(clientMode);
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
                    Mode = clientMode,
                    MaxDynamitePerLife = clientDynamite,
                    Death = opt.Death,
                    PaceMs = opt.PaceMs,
                    CohortSize = count,
                    Respawn = opt.Respawn,
                    MaxLives = opt.MaxLives,
                    RespawnDelayMs = opt.RespawnDelayMs,
                    RespawnTimeoutMs = opt.RespawnTimeoutMs,
                    LocalBindIp = GameJoinClient.LoopbackBindFor(clientId, attempt),
                    Bench = bench,
                    OnLifeStarted = entityId =>
                        provisioner.Enqueue($"give {entityId} thrownDynamite 3", entityId, log),
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
                        RunReport.FaultLine($"join#{clientId} attempt {attempt}", ex));
                }
                last = c.State;
                totals.AddCounters(last);
                if (attempt > 1) totals.RejoinCount++;

                // Intentional end of budget (walked until timeout) or hard fail without join.
                // Recompute remaining fresh: a join attempt can burn most of the
                // budget, so the pre-attempt value would let the loop overshoot.
                DeathCause cause = last.DeathCause;
                long remainMs = opt.TimeoutMs - sessionSw.ElapsedMilliseconds;
                if (cause == DeathCause.TimeoutAlive || remainMs < 15_000)
                    break;
                // Backoff + deterministic per-client jitter so 1000 bots that fail
                // together do not retry in unison (thundering herd on the server).
                // clientId-based jitter keeps runs reproducible (no RNG).
                int backoff(int baseMs, int step) =>
                    (int)Math.Min(15_000, baseMs + attempt * step + clientId % 500);
                if (last.EverJoined && (last.Stage == JoinStage.Disconnected
                    || cause == DeathCause.ServerDisconnect))
                {
                    log?.Invoke(
                        $"[{DateTime.UtcNow:O}] REJOIN client={clientId} attempt={attempt} " +
                        $"cause={DeathCauseNames.Of(cause)} remainingMs={remainMs}");
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
        Dictionary<string, object?> BuildStatsPayload(int total, int pass, in CohortCounters c, IEnumerable<int> joinSamples)
        {
            double rate = total == 0 ? 0 : (double)pass / total;
            var ping = PingStats.Summary();
            var (joinCount, joinP50, joinP95, joinMax) = JoinLatency.Summary(joinSamples);
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
                ["dynamite"] = c.Dynamite,
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
                ["workload"] = WorkloadBlock(
                    opt.ActionSeed, opt.ActionCount, opt.PaceMs, count, concurrency, opt.ClientId,
                    joinRampMs, opt.MaxDynamitePerLife, opt.Respawn, opt.MaxLives,
                    spawnZombies, killFallback, spawnEntity, spawnPerPlayer,
                    spawnEveryMs, hordeEveryMs, hordeWaves),
                ["pingSamples"] = ping.count,
                // Above 1 the ping percentiles below come from one sample in
                // pingSampleStride, scaled to the whole run.
                ["pingSampleStride"] = ping.stride,
                ["joinMsSamples"] = joinCount,
                ["joinMsP50"] = joinP50,
                ["joinMsP95"] = joinP95,
                ["joinMsMax"] = joinMax,
                ["pingAvgMs"] = Math.Round(ping.avg, 1),
                ["pingP50Ms"] = ping.p50,
                ["pingP95Ms"] = ping.p95,
                ["pingMaxMs"] = ping.max,
                ["pingSpikesOver150Ms"] = ping.spikes,
            };
        }

        if (count == 1)
        {
            // Same run header as the cohort lane: a single-bot run also produces
            // a stats json and a manifest, and its console lines have to name
            // the run they belong to.
            Console.WriteLine(
                $"[{DateTime.UtcNow:O}] JOIN_LOAD count=1 concurrency=1 host={opt.Host}:{opt.Port} " +
                $"mode={opt.Mode} death={opt.Death} actions={opt.ActionCount} seed={opt.ActionSeed} " +
                $"timeoutMs={opt.TimeoutMs}{scenarioTag} bind={opt.LocalBindIp ?? "0.0.0.0"}");
            var lines = new List<string>();
            // --quiet drops the console echo only; --log still gets every line.
            Action<string> log = s =>
            {
                if (!quiet) Console.WriteLine(s);
                lines.Add(s);
            };
            var (rc, sm) = RunWithRejoin(opt.ClientId, log);
            spawnCts.Cancel();
            AwaitTeardown("zombie_spawn", spawnTask);
            AwaitTeardown("wandering_horde", hordeTask);
            if (!string.IsNullOrEmpty(logPath))
                RunReport.WriteArtifact("log", logPath, () => RunReport.WriteLines(logPath, lines));
            // Single-bot runs still write stats-json (and the run manifest when
            // asked) so the bench lane evidence is uniform (probe-15s/join-fast/
            // join-probe/horde-lite are count=1).
            var payload1 = BuildStatsPayload(1, rc == 0 ? 1 : 0, CohortCounters.FromState(sm), new[] { sm.JoinMs });
            if (!string.IsNullOrEmpty(statsJsonPath))
                RunReport.WriteArtifact("stats", statsJsonPath, () =>
                    File.WriteAllText(statsJsonPath,
                        System.Text.Json.JsonSerializer.Serialize(payload1, ArtifactJsonOpts) + "\n"));
            if (!string.IsNullOrEmpty(runManifestPath))
                WriteJoinManifest(runManifestPath, scenarioId, payload1, new[] { (opt.ClientId, rc, sm) });
            // The pass-rate gate is the exit-code contract at every cohort size:
            // a single bot that failed is 0/1, which a 0 bar passes.
            double singlePassRate = rc == 0 ? 1.0 : 0.0;
            if (JoinGatePass(rc == 0 ? 1 : 0, 1, minPassRate))
            {
                Console.WriteLine("PASS: join total=1 passRate=100.00%");
                return 0;
            }
            Console.Error.WriteLine(
                $"FAIL: passRate={singlePassRate:P2} < minPassRate={minPassRate:P2}");
            return 1;
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

        // Multi join: unique 127.x.x.x binds + bounded concurrency (dedicated rate-limit is per IP)
        Console.WriteLine(
            $"[{DateTime.UtcNow:O}] JOIN_LOAD count={count} concurrency={concurrency} " +
            $"host={opt.Host}:{opt.Port} actions={opt.ActionCount} mode={opt.Mode} death={opt.Death} " +
            $"seed={opt.ActionSeed}{scenarioTag} " +
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
        // Per-bot outcome: the session's final state snapshot already carries the
        // aggregate counters (RunWithRejoin folds every rejoin attempt into it),
        // so storing the object keeps one source of truth for the summary,
        // stats-json, run manifest, and deaths CSV.
        var results = new System.Collections.Concurrent.ConcurrentBag<(int id, int rc, JoinStateMachine s)>();
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
                Action<string>? log = i < 3 && !quiet ? Console.WriteLine : null;
                try
                {
                    var (rc, s) = RunWithRejoin(id, log);
                    results.Add((id, rc, s));
                }
                catch (Exception ex)
                {
                    // Unconditional: a cohort-wide fault must never be invisible
                    // just because the bot's console log was throttled off.
                    Console.Error.WriteLine(RunReport.FaultLine($"join#{id} session", ex));
                    var failState = new JoinStateMachine
                    {
                        EntityId = -1,
                        DeathCause = DeathCause.Exception,
                        BotModeName = opt.Mode.ToString(),
                    };
                    failState.Fail("exception");
                    results.Add((id, 1, failState));
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
        // One pass folds every bot's final snapshot (RunWithRejoin already
        // aggregated rejoin attempts into it); all sums are long so the totals
        // across 1000 bots on a multi-day run cannot wrap int.MaxValue.
        CohortCounters cohort = CohortCounters.Sum(results.Select(r => r.s));
        double rate = count == 0 ? 0 : (double)pass / count;

        var byCause = results
            .GroupBy(r => r.s.DeathCause)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{DeathCauseNames.Of(g.Key)}={g.Count()}")
            .ToList();
        // One cause table for both renderings. The console line and the stats
        // JSON used to name their buckets separately, so four causes the
        // console reported were computed and then dropped from the artifact.
        // Keys are stable report-schema names; every DeathCause lands in one.
        var deathBuckets = new (string Key, int Count)[]
        {
            ("world_killed", results.Count(r =>
                r.s.DeathCause is DeathCause.WorldKilled or DeathCause.WorldDeath)),
            ("world_drown", results.Count(r => r.s.DeathCause == DeathCause.WorldDrown)),
            ("world_radiation", results.Count(r => r.s.DeathCause == DeathCause.WorldRadiation)),
            ("timeout_alive", results.Count(r => r.s.DeathCause == DeathCause.TimeoutAlive)),
            ("disconnect", results.Count(r => r.s.DeathCause == DeathCause.ServerDisconnect)),
            ("self_kill", results.Count(r => r.s.DeathCause
                is DeathCause.DrownFatal or DeathCause.Suicide
                or DeathCause.SuicideFallback or DeathCause.KilledExternal)),
            ("respawn_timeout", results.Count(r => r.s.DeathCause == DeathCause.RespawnTimeout)),
            ("exception", results.Count(r => r.s.DeathCause == DeathCause.Exception)),
        };

        var (joinCount, joinP50, joinP95, joinMax) = JoinLatency.Summary(results.Select(r => r.s.JoinMs));
        var report =
            $"JOIN_SUMMARY total={count} pass={pass} fail={count - pass} passRate={rate:P2} mode={opt.Mode} death={opt.Death} respawn={opt.Respawn}\n" +
            $"JOIN_LATENCY joined={joinCount} p50Ms={joinP50} p95Ms={joinP95} maxMs={joinMax}\n" +
            $"JOIN_ACTIONS walks={cohort.Walks} jumps={cohort.Jumps} crouch={cohort.Crouches} aim={cohort.Aims} turn={cohort.Turns} " +
            $"strafe={cohort.Strafes} look={cohort.Looks} chat={cohort.Chats} break={cohort.Breaks} " +
            $"dynamite={cohort.Dynamite} attack={cohort.Attacks} " +
            $"diedClients={cohort.DiedClients} totalDeaths={cohort.TotalDeaths} totalRespawns={cohort.TotalRespawns} " +
            $"totalRejoins={cohort.TotalRejoins}\n" +
            $"DEATH_STATS total={count} died={cohort.DiedClients} alive={count - cohort.DiedClients} " +
            string.Join(" ", deathBuckets.Select(b => $"{b.Key}={b.Count}")) + "\n" +
            $"DEATH_HISTOGRAM {string.Join(" ", byCause)}\n" +
            string.Join("\n", results.OrderBy(r => r.id).Take(30).Select(r =>
                $"  id={r.id} rc={r.rc} mode={r.s.BotModeName} entity={r.s.EntityId} joinMs={r.s.JoinMs} w={r.s.WalkActions} j={r.s.JumpActions} " +
                $"deaths={r.s.DeathCount} respawns={r.s.RespawnCount} rejoins={r.s.RejoinCount} " +
                $"lastDied={r.s.Died} cause={DeathCauseNames.Of(r.s.DeathCause)}"));
        if (bench is { } b)
        {
            var (wStart, _) = b.WindowBounds;
            var (wActions, wDeaths, wRespawns) = b.WindowCounts;
            var (activeMin, activeMax) = b.ActiveBounds;
            double aps = b.WindowMs > 0 ? wActions * 1000.0 / b.WindowMs : 0;
            double jps = wStart > 0 ? pass * 1000.0 / wStart : 0;
            Console.WriteLine(
                $"BENCH_SUMMARY warmupMs={b.WarmupMs} windowMs={b.WindowMs} " +
                $"actionsInWindow={wActions} actionsPerSec={aps:0.00} " +
                $"deathsInWindow={wDeaths} respawnsInWindow={wRespawns} " +
                $"joinRatePerSec={jps:0.000} activeMin={activeMin} activeMax={activeMax} " +
                $"activeAtWindowStart={b.ActiveAtWindowStart} activeAtWindowEnd={b.ActiveAtWindowEnd}");
        }
        Console.WriteLine(report);
        if (!string.IsNullOrEmpty(statsJsonPath) || !string.IsNullOrEmpty(runManifestPath))
        {
            var payload = BuildStatsPayload(count, pass, cohort, results.Select(r => r.s.JoinMs));
            foreach (var (key, causeCount) in deathBuckets)
                payload[key] = causeCount;
            if (bench is { } b2)
            {
                var (wStart, wEnd) = b2.WindowBounds;
                var (wActions, wDeaths, wRespawns) = b2.WindowCounts;
                var (activeMin, activeMax) = b2.ActiveBounds;
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
                    ["activeMin"] = activeMin,
                    ["activeMax"] = activeMax,
                    ["activeAtWindowStart"] = b2.ActiveAtWindowStart,
                    ["activeAtWindowEnd"] = b2.ActiveAtWindowEnd,
                    ["activeCurve"] = b2.ActiveCurve().Select(s => new[] { s.Ms, s.Active }).ToList(),
                };
            }
            if (!string.IsNullOrEmpty(statsJsonPath))
            {
                RunReport.WriteArtifact("stats", statsJsonPath, () =>
                    File.WriteAllText(statsJsonPath,
                        System.Text.Json.JsonSerializer.Serialize(payload, ArtifactJsonOpts) + "\n"));
            }
            if (!string.IsNullOrEmpty(runManifestPath))
                WriteJoinManifest(runManifestPath, scenarioId, payload, results.ToList());
        }
        if (!string.IsNullOrEmpty(logPath))
        {
            RunReport.WriteArtifact("log", logPath, () => File.WriteAllText(logPath, report + "\n"));
            var csvPath = Path.ChangeExtension(logPath, "_deaths.csv");
            var csv = new System.Text.StringBuilder();
            csv.Append(
                "id,rc,mode,stage,entityId,walks,jumps,crouches,aims,turns,strafes,looks,chats," +
                "breaks,dynamite,attacks,drowns,suicides,killed,died,deathCause,deathCount,respawnCount,rejoinCount\n");
            foreach (var r in results.OrderBy(x => x.id))
            {
                csv.Append(
                    $"{r.id},{r.rc},{r.s.BotModeName},{r.s.Stage},{r.s.EntityId},{r.s.WalkActions},{r.s.JumpActions}," +
                    $"{r.s.CrouchActions},{r.s.AimActions},{r.s.TurnActions},{r.s.StrafeActions},{r.s.LookActions},{r.s.ChatActions}," +
                    $"{r.s.BreakBlockActions},{r.s.DynamiteActions},{r.s.AttackActions},{r.s.DrownActions},{r.s.SuicideActions}," +
                    $"{r.s.KilledActions},{r.s.Died}," +
                    $"{DeathCauseNames.Of(r.s.DeathCause)},{r.s.DeathCount},{r.s.RespawnCount},{r.s.RejoinCount}\n");
            }
            RunReport.WriteArtifact("DEATH_CSV", csvPath, () => File.WriteAllText(csvPath, csv.ToString()));
        }
        // Verdict, same shape as the single-bot lane: the pass line is the
        // result a caller reads, the fail line is a diagnostic for stderr.
        bool gatePass = JoinGatePass(pass, count, minPassRate);
        (gatePass ? Console.Out : Console.Error).WriteLine(
            gatePass
                ? $"PASS: join total={count} passRate={rate:P2}"
                : $"FAIL: passRate={rate:P2} < minPassRate={minPassRate:P2}");
        return gatePass ? 0 : 1;
    }

    /// <summary>One run manifest client row (shared by the single- and
    /// multi-bot writers so the schema cannot drift).</summary>
    static Dictionary<string, object?> ManifestClientRow(int id, int rc, JoinStateMachine s) => new()
    {
        ["id"] = id,
        ["rc"] = rc,
        ["mode"] = s.BotModeName,
        ["entityId"] = s.EntityId,
        ["walks"] = s.WalkActions,
        ["deaths"] = s.DeathCount,
        ["respawns"] = s.RespawnCount,
        ["died"] = s.Died,
        // The report's cause vocabulary is the DeathCauseNames table; the raw
        // enum would serialize as a PascalCase identifier every other artifact
        // spells in snake_case.
        ["deathCause"] = DeathCauseNames.Of(s.DeathCause),
    };

    /// <summary>Join run manifest (schema 7dtd.loadgen.run.v1): cohort payload
    /// plus one row per bot. Shared by the single- and multi-bot paths so a
    /// --run-manifest request is honored at any cohort size.</summary>
    internal static void WriteJoinManifest(
        string path, string scenarioId,
        Dictionary<string, object?> payload, IEnumerable<(int id, int rc, JoinStateMachine s)> rows)
    {
        var run = new Dictionary<string, object?>
        {
            ["schema"] = "7dtd.loadgen.run.v1",
            ["kind"] = "join",
            ["scenarioId"] = string.IsNullOrEmpty(scenarioId) ? null : scenarioId,
            ["cohort"] = payload,
            ["clients"] = rows.OrderBy(r => r.id).Select(r => ManifestClientRow(r.id, r.rc, r.s)).ToList(),
            ["product"] = new Dictionary<string, object?>
            {
                ["name"] = "RealEarth",
                ["priorityFocus"] = "P0-P1",
                ["notes"] = "Tall Y + inject soak when dedicated expanded",
            },
        };
        RunReport.WriteArtifact("run_manifest", path, () =>
            File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(run, ArtifactJsonOpts) + "\n"));
    }

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

    /// <summary>Cancellable sleep that reports whether to keep looping.
    /// The catch is catch-all on purpose: .Wait() wraps delay cancellation in
    /// AggregateException, and letting that propagate would fault the pressure
    /// task on every clean teardown and make AwaitTeardown log a spurious ERROR
    /// into the run log. The token, not the exception, decides the answer.</summary>
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
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:O}] TELNET {label} err {RunReport.FaultText(label, ex)}");
                    if (!NappableDelay(errorBackoffMs, ct)) break;
                }
            }
        });
}
