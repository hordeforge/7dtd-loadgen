using System.Diagnostics;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SevenDTD.LoadGen;

/// <summary>
/// 7DTD load-test client: LiteNetLib probe, full join path, bot walk/death/respawn.
/// </summary>
public static class Program
{
    /// <summary>No-op logger so game LiteNetLib never calls UnityEngine.Debug under pure .NET.</summary>
    sealed class NullNetLogger : INetLogger
    {        public void WriteNet(NetLogLevel level, string str, params object[] args) { }
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

        return mode switch
        {
            "self-test-join" => RunSelfTestJoin(args),
            "self-test" => SelfTest.Run(args),
            "join" => RunJoin(args),
            _ => RunProbe(args),
        };
    }

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
            File.WriteAllLines(logPath, lines.Concat(sm.Log));
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
            var dir = Path.GetDirectoryName(Path.GetFullPath(runManifestPath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(
                runManifestPath,
                System.Text.Json.JsonSerializer.Serialize(run, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            Log($"run_manifest: {runManifestPath}");
        }
        return rc;
    }

    static int RunJoin(string[] args)
    {
        var opt = new GameJoinClient.Options();
        string? logPath = null;
        string? statsJsonPath = null;
        string? runManifestPath = null;
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
        string telnetPassword = "retest";
        int spawnEveryMs = 20_000;
        int spawnPerPlayer = 4;
        string spawnEntity = "zombieBoe";
        // Wandering hordes: periodic scout-horde bursts that spawn at distance and
        // path in as a group. 0 = off. Slower cadence than the per-player trickle.
        int hordeEveryMs = 0;
        int hordeWaves = 3;
        // Weighted per-bot mode mix, e.g. "traverse:35,combat:20,bait:15". Empty
        // -> whole cohort uses opt.Mode. Assigned deterministically by client id
        // so the profile is repeatable.
        var botMix = new List<(ActionLoop.BotMode mode, int weight)>();

        // Named workload profiles (TODO "Add named workload profiles"): preset
        // cohort defaults applied before the arg loop so an explicit flag on the
        // same command line always overrides the profile.
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
            case "":
                break;
            default:
                Console.Error.WriteLine(
                    $"FAIL: unknown --profile '{profile}' (probe|join-burst|steady-wander|death-soak|mixed)");
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
                    if (mode == ActionLoop.BotMode.Demolition && opt.MaxDynamitePerLife == 3)
                        opt.MaxDynamitePerLife = 200;
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
        }

        if (count < 1) count = 1;
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
            spawnTask = Task.Run(() =>
            {
                // First wave after bots have a chance to join
                try { Task.Delay(8_000, spawnCts.Token).Wait(); } catch { return; }
                while (!spawnCts.IsCancellationRequested)
                {
                    try
                    {
                        // Fresh telnet each wave — long sessions drop half-open sockets.
                        using var admin = new TelnetAdmin(telnetHost, telnetPort, telnetPassword, Console.WriteLine)
                        {
                            KillFallback = killFallback,
                        };
                        if (admin.Connect())
                            admin.SpawnZombiesNearPlayers(spawnEntity, spawnPerPlayer);
                        Task.Delay(Math.Max(5_000, spawnEveryMs), spawnCts.Token).Wait();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:O}] TELNET spawn err: {ex.Message}");
                        try { Task.Delay(10_000, spawnCts.Token).Wait(); } catch { break; }
                    }
                }
            });
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
            hordeTask = Task.Run(() =>
            {
                try { Task.Delay(20_000, spawnCts.Token).Wait(); } catch { return; }
                while (!spawnCts.IsCancellationRequested)
                {
                    try
                    {
                        using var admin = new TelnetAdmin(telnetHost, telnetPort, telnetPassword, Console.WriteLine);
                        if (admin.Connect()) admin.SpawnWanderingHorde(hordeWaves, 2);
                        Task.Delay(Math.Max(15_000, hordeEveryMs), spawnCts.Token).Wait();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:O}] TELNET horde err: {ex.Message}");
                        try { Task.Delay(15_000, spawnCts.Token).Wait(); } catch { break; }
                    }
                }
            });
        }

        // Per-bot session: rejoin on early disconnect until overall wall clock expires.
        (int rc, JoinStateMachine s) RunWithRejoin(int clientId, Action<string>? log)
        {
            var sessionSw = Stopwatch.StartNew();
            int accWalks = 0, accJumps = 0, accCrouch = 0, accAim = 0, accTurn = 0;
            int accStrafe = 0, accLook = 0, accChat = 0, accBreak = 0, accAtk = 0;
            int accDrown = 0, accSuicide = 0, accKilled = 0, accDeaths = 0, accRespawns = 0, accRejoins = 0;
            int lastRc = 1;
            JoinStateMachine last = new();
            int attempt = 0;
            var clientMode = ModeForClient(clientId);
            int clientDynamite =
                clientMode == ActionLoop.BotMode.Demolition && opt.MaxDynamitePerLife <= 3
                    ? 200
                    : opt.MaxDynamitePerLife;
            while (sessionSw.ElapsedMilliseconds + 5_000 < opt.TimeoutMs)
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
                    WanderUntilDeath = clientMode == ActionLoop.BotMode.Wander && opt.WanderUntilDeath,
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
                        }
                        catch (Exception ex)
                        {
                            log?.Invoke($"[{DateTime.UtcNow:O}] DYNAMITE_GIVE entity={entityId} failed={ex.Message}");
                        }
                    },
                    Log = log,
                };
                var c = new GameJoinClient();
                try
                {
                    lastRc = c.Run(o);
                }
                catch (Exception ex)
                {
                    lastRc = 1;
                    log?.Invoke($"[{DateTime.UtcNow:O}] join#{clientId} EX: {ex.GetType().Name}: {ex.Message}");
                }
                last = c.State;
                accWalks += last.WalkActions;
                accJumps += last.JumpActions;
                accCrouch += last.CrouchActions;
                accAim += last.AimActions;
                accTurn += last.TurnActions;
                accStrafe += last.StrafeActions;
                accLook += last.LookActions;
                accChat += last.ChatActions;
                accBreak += last.BreakBlockActions;
                accAtk += last.AttackActions;
                accDrown += last.DrownActions;
                accSuicide += last.SuicideActions;
                accKilled += last.KilledActions;
                accDeaths += last.DeathCount;
                accRespawns += last.RespawnCount;
                accRejoins += attempt > 1 ? 1 : 0; // every retry past the first is a rejoin

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
            // Fold aggregates into last state snapshot for reporting.
            last.WalkActions = accWalks;
            last.JumpActions = accJumps;
            last.CrouchActions = accCrouch;
            last.AimActions = accAim;
            last.TurnActions = accTurn;
            last.StrafeActions = accStrafe;
            last.LookActions = accLook;
            last.ChatActions = accChat;
            last.BreakBlockActions = accBreak;
            last.AttackActions = accAtk;
            last.DrownActions = accDrown;
            last.SuicideActions = accSuicide;
            last.KilledActions = accKilled;
            last.DeathCount = accDeaths;
            last.RespawnCount = accRespawns;
            last.RejoinCount = accRejoins;
            return (lastRc, last);
        }

        if (count == 1)
        {
            var lines = new List<string>();
            Action<string> log = s => { Console.WriteLine(s); lines.Add(s); };
            var (rc, sm) = RunWithRejoin(opt.ClientId, log);
            spawnCts.Cancel();
            try { spawnTask?.Wait(2000); } catch { /* ignore */ }
            try { hordeTask?.Wait(2000); } catch { /* ignore */ }
            if (!string.IsNullOrEmpty(logPath))
                File.WriteAllLines(logPath, lines);
            _ = sm;
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
        if (spawnZombies || killFallback)
            Console.WriteLine(
                $"[{DateTime.UtcNow:O}] WARNING: server-side pressure active - " +
                (spawnZombies ? "telnet zombie spawning" : "") +
                (spawnZombies && killFallback ? " and " : "") +
                (killFallback ? "admin kill fallback" : "") +
                ". These modify the world and raise server load; use --no-spawn-zombies " +
                "and/or --no-kill-fallback for a pure join/action measurement.");
        var results = new System.Collections.Concurrent.ConcurrentBag<(
            int id, int rc, JoinStage stage, int walks, int jumps, int crouches, int aims, int turns,
            int strafes, int looks, int chats, int breaks, int attacks,
            int drowns, int suicides, int killed, bool died, string deathCause, int entityId, string mode,
            int deathCount, int respawnCount, int rejoinCount)>();
        var gate = new SemaphoreSlim(concurrency);
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            if (joinRampMs > 0 && count > 1)
                await Task.Delay(RampDelayMs(i, count, joinRampMs)).ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                int id = opt.ClientId + i;
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
                    if (i < 5)
                        Console.WriteLine($"[{DateTime.UtcNow:O}] join#{id} EX: {ex.GetType().Name}: {ex.Message}");
                    results.Add((id, 1, JoinStage.Failed, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, "exception", -1, opt.Mode.ToString(), 0, 0, 0));
                }
            }
            finally { gate.Release(); }
        })).ToArray();
        Task.WaitAll(tasks);
        spawnCts.Cancel();
        try { spawnTask?.Wait(2000); } catch { /* ignore */ }
        try { hordeTask?.Wait(2000); } catch { /* ignore */ }

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
        Console.WriteLine(report);
        if (!string.IsNullOrEmpty(statsJsonPath) || !string.IsNullOrEmpty(runManifestPath))
        {
            var payload = new Dictionary<string, object?>
            {
                ["schema"] = "7dtd.loadgen.stats.v1",
                ["scenarioId"] = string.IsNullOrEmpty(scenarioId) ? null : scenarioId,
                ["host"] = opt.Host,
                ["port"] = opt.Port,
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["total"] = count,
                ["pass"] = pass,
                ["fail"] = count - pass,
                ["passRate"] = rate,
                ["mode"] = opt.Mode.ToString(),
                ["death"] = opt.Death.ToString(),
                ["walks"] = walks,
                ["jumps"] = jumps,
                ["crouches"] = crouches,
                ["aims"] = aims,
                ["turns"] = turns,
                ["strafes"] = strafes,
                ["looks"] = looks,
                ["chats"] = chats,
                ["breaks"] = breaks,
                ["attacks"] = attacks,
                ["drowns"] = drowns,
                ["suicides"] = suicides,
                ["killed"] = killed,
                ["diedClients"] = died,
                ["totalDeaths"] = totalDeaths,
                ["totalRespawns"] = totalRespawns,
                ["totalRejoins"] = totalRejoins,
                ["world_killed"] = worldKilled,
                ["timeout_alive"] = timedOut,
                ["disconnect"] = disc,
                ["minPassRate"] = minPassRate,
                ["gatePass"] = JoinGatePass(pass, count, minPassRate),
            };
            var ping = PingStats.Summary();
            payload["pingSamples"] = ping.count;
            payload["pingAvgMs"] = Math.Round(ping.avg, 1);
            payload["pingP50Ms"] = ping.p50;
            payload["pingP95Ms"] = ping.p95;
            payload["pingMaxMs"] = ping.max;
            payload["pingSpikesOver150Ms"] = ping.spikes;
            var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            if (!string.IsNullOrEmpty(statsJsonPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statsJsonPath))!);
                File.WriteAllText(statsJsonPath, System.Text.Json.JsonSerializer.Serialize(payload, jsonOpts) + "\n");
                Console.WriteLine($"stats: {statsJsonPath}");
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
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(runManifestPath))!);
                File.WriteAllText(runManifestPath, System.Text.Json.JsonSerializer.Serialize(run, jsonOpts) + "\n");
                Console.WriteLine($"run_manifest: {runManifestPath}");
            }
        }
        if (!string.IsNullOrEmpty(logPath))
        {
            File.WriteAllText(logPath, report + "\n");
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
            File.WriteAllText(csvPath, csv.ToString());
            Console.WriteLine($"DEATH_CSV {csvPath}");
        }
        return JoinGatePass(pass, count, minPassRate) ? 0 : 1;
    }

    static int RunProbe(string[] args)
    {
        string host = "127.0.0.1";
        int port = 26900;
        string key = "";
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
            else if (args[i] == "--ramp-ms" && i + 1 < args.Length) rampMs = int.Parse(args[++i]);
            else if (args[i] == "--quiet") quiet = true;
        }

        if (count < 1) count = 1;
        if (count == 1)
        {
            Action<string>? log = quiet ? null : Console.WriteLine;
            var result = LiteNetProbe.Run(host, port, key, timeoutMs, clientId, log);
            if (!string.IsNullOrEmpty(logPath))
                LiteNetProbe.FlushLog(logPath, result.Lines);
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
            File.WriteAllText(logPath, summary.ToReport());
        Console.WriteLine(summary.ToReport());
        if (summary.PassRate + 1e-9 < minPassRate)
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
            "  --profile probe|join-burst|steady-wander|death-soak|mixed\n" +
            "      preset cohort defaults; explicit flags override per key\n" +
            "  --mode wander|mixed|chatty|combat|patrol|chaos\n" +
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
            "  --golden-wire       Assert package body layouts vs Assembly-CSharp IL sizes\n" +
            "Notes:\n" +
            "  Walk → world kill → DEATH → respawn → walk again until --timeout. No self-kill.\n" +
            "  Default timeout 1 hour. Rejoins on early disconnect. Telnet zed spawn for empty worlds.\n" +
            "Examples:\n" +
            "  7dtd-loadgen --join --host 127.0.0.1 --port 26902 --count 8\n" +
            "  7dtd-loadgen --join --count 4 --timeout 1800000 --max-lives 10\n" +
            "  7dtd-loadgen --self-test-join --actions 24\n" +
            "  7dtd-loadgen --golden-wire\n");
    }
}

// --- Probe / load types (unchanged surface for prior tests) ---

public sealed class ProbeResult
{
    public required bool Pass { get; init; }
    public required HashSet<string> Stages { get; init; }
    public required bool Connected { get; init; }
    public required int Packets { get; init; }
    public string? DisconnectReason { get; init; }
    public required List<string> Lines { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed class LoadSummary
{
    public int Total { get; init; }
    public int Pass { get; init; }
    public int Fail { get; init; }
    public double PassRate => Total == 0 ? 0 : (double)Pass / Total;
    public long ElapsedMs { get; init; }
    public long P50Ms { get; init; }
    public long P95Ms { get; init; }
    public long P99Ms { get; init; }
    public int Connected { get; init; }
    public int ProtocolProgress { get; init; }
    public Dictionary<string, int> StageCounts { get; init; } = new();
    public List<string> FailSamples { get; init; } = new();

    public string ToReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"LOAD_SUMMARY total={Total} pass={Pass} fail={Fail} passRate={PassRate:P2}");
        sb.AppendLine($"LOAD_TIMING elapsedMs={ElapsedMs} p50={P50Ms} p95={P95Ms} p99={P99Ms}");
        sb.AppendLine($"LOAD_CONN connected={Connected} protocolProgress={ProtocolProgress}");
        if (StageCounts.Count > 0)
            sb.AppendLine("LOAD_STAGES " + string.Join(" ", StageCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var f in FailSamples.Take(20))
            sb.AppendLine($"LOAD_FAIL_SAMPLE {f}");
        return sb.ToString();
    }
}

public static class LoadRunner
{
    public static LoadSummary Run(
        string host, int port, string key, int timeoutMs, int count, int concurrency,
        int rampMs, bool quiet, int idBase = 1)
    {
        concurrency = Math.Max(1, Math.Min(concurrency, count));
        var results = new System.Collections.Concurrent.ConcurrentBag<ProbeResult>();
        var gate = new SemaphoreSlim(concurrency, concurrency);
        var swAll = Stopwatch.StartNew();
        var tasks = new Task[count];
        int nextId = 0;
        for (int i = 0; i < count; i++)
        {
            int slot = i;
            tasks[i] = Task.Run(async () =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (rampMs > 0 && count > 1)
                    {
                        int delay = (int)((long)slot * rampMs / count);
                        if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);
                    }
                    int id = idBase + Interlocked.Increment(ref nextId) - 1;
                    Action<string>? log = quiet ? null : (msg => { if (id < idBase + 8) Console.WriteLine(msg); });
                    results.Add(LiteNetProbe.Run(host, port, key, timeoutMs, id, log, keepLines: !quiet && id < idBase + 8));
                }
                finally { gate.Release(); }
            });
        }
        Task.WaitAll(tasks);
        swAll.Stop();
        var list = results.ToList();
        var latencies = list.Select(r => r.ElapsedMs).OrderBy(x => x).ToArray();
        long Pct(double p)
        {
            if (latencies.Length == 0) return 0;
            int idx = (int)Math.Clamp(Math.Ceiling(p * latencies.Length) - 1, 0, latencies.Length - 1);
            return latencies[idx];
        }
        var stageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int connected = 0, protocolProgress = 0;
        var fails = new List<string>();
        foreach (var r in list)
        {
            if (r.Connected) connected++;
            if (r.Pass) protocolProgress++;
            foreach (var s in r.Stages)
            {
                stageCounts.TryGetValue(s, out int c);
                stageCounts[s] = c + 1;
            }
            if (!r.Pass && fails.Count < 32)
                fails.Add($"stages=[{string.Join(",", r.Stages.OrderBy(x => x))}] disc={r.DisconnectReason ?? "none"}");
        }
        int pass = list.Count(r => r.Pass);
        return new LoadSummary
        {
            Total = list.Count,
            Pass = pass,
            Fail = list.Count - pass,
            ElapsedMs = swAll.ElapsedMilliseconds,
            P50Ms = Pct(0.50),
            P95Ms = Pct(0.95),
            P99Ms = Pct(0.99),
            Connected = connected,
            ProtocolProgress = protocolProgress,
            StageCounts = stageCounts,
            FailSamples = fails,
        };
    }
}

public static class LiteNetProbe
{
    public static ProbeResult Run(
        string host, int port, string key, int timeoutMs, int clientId,
        Action<string>? writeLine = null, bool keepLines = true)
    {
        var lines = keepLines ? new List<string>() : new List<string>(0);
        var sw = Stopwatch.StartNew();
        void Log(string msg)
        {
            string line = $"[{DateTime.UtcNow:O}] [fake#{clientId}] {msg}";
            writeLine?.Invoke(line);
            if (keepLines) lines.Add(line);
        }
        Log($"loadtest client starting host={host} port={port} timeoutMs={timeoutMs}");
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 500;
            udp.Connect(host, port);
            Log("STAGE udp_socket_open: ok");
        }
        catch (Exception ex)
        {
            Log($"STAGE udp_socket_open: fail {ex.GetType().Name}: {ex.Message}");
            return new ProbeResult
            {
                Pass = false, Stages = new HashSet<string>(), Connected = false, Packets = 0,
                Lines = lines, ElapsedMs = sw.ElapsedMilliseconds,
            };
        }

        var listener = new EventBasedNetListener();
        var net = new NetManager(listener) { AutoRecycle = true, DisconnectTimeout = timeoutMs, UpdateTime = 15 };
        bool connected = false, disconnected = false;
        string? disconnectReason = null;
        int packets = 0;
        var stages = new HashSet<string> { "udp_socket_open" };
        listener.PeerConnectedEvent += peer =>
        {
            connected = true;
            stages.Add("litenet_peer_connected");
            Log($"STAGE litenet_peer_connected: {peer.Address}:{peer.Port}");
        };
        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            disconnected = true;
            disconnectReason = info.Reason.ToString();
            stages.Add("litenet_peer_disconnected");
            Log($"STAGE litenet_peer_disconnected: reason={info.Reason}");
        };
        listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            packets++;
            stages.Add("litenet_receive");
            if (reader.AvailableBytes > 0) stages.Add("protocol_bytes");
            reader.Recycle();
        };
        if (!net.Start())
        {
            Log("STAGE litenet_start: fail");
            return Fail(lines, stages, connected, packets, disconnectReason, sw.ElapsedMilliseconds);
        }
        stages.Add("litenet_start");
        Log("STAGE litenet_start: ok");
        var data = new NetDataWriter();
        if (!string.IsNullOrEmpty(key)) data.Put(key);
        var peer = net.Connect(host, port, data);
        if (peer == null)
        {
            Log("STAGE litenet_connect_call: fail");
            net.Stop();
            return Fail(lines, stages, connected, packets, disconnectReason, sw.ElapsedMilliseconds);
        }
        stages.Add("litenet_connect_call");
        Log("STAGE litenet_connect_call: ok");
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            net.PollEvents();
            if (connected && (packets > 0 || sw.ElapsedMilliseconds > 1500)) break;
            if (disconnected && !connected) break;
            Thread.Sleep(10);
        }
        net.PollEvents();
        net.Stop();
        sw.Stop();
        bool pastSocket = stages.Contains("litenet_peer_connected") || stages.Contains("litenet_receive")
            || stages.Contains("litenet_peer_disconnected") || stages.Contains("protocol_bytes");
        bool pass = pastSocket || (stages.Contains("litenet_connect_call") && (connected || disconnectReason != null));
        Log($"SUMMARY stages=[{string.Join(",", stages.OrderBy(s => s))}] connected={connected} packets={packets}");
        return new ProbeResult
        {
            Pass = pass, Stages = stages, Connected = connected, Packets = packets,
            DisconnectReason = disconnectReason, Lines = lines, ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    static ProbeResult Fail(List<string> lines, HashSet<string> stages, bool connected, int packets, string? disc, long ms) =>
        new() { Pass = false, Stages = stages, Connected = connected, Packets = packets, DisconnectReason = disc, Lines = lines, ElapsedMs = ms };

    public static void FlushLog(string? path, List<string> lines)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(path, lines);
        }
        catch { /* best effort */ }
    }
}

static class SelfTest
{
    public static int Run(string[] args)
    {
        int port = 0, count = 1, concurrency = 0, timeoutMs = 4000;
        double minPassRate = 0.95;
        string? logPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
            else if (args[i] == "--count" && i + 1 < args.Length) count = int.Parse(args[++i]);
            else if (args[i] == "--concurrency" && i + 1 < args.Length) concurrency = int.Parse(args[++i]);
            else if (args[i] == "--min-pass-rate" && i + 1 < args.Length) minPassRate = double.Parse(args[++i]);
            else if (args[i] == "--timeout" && i + 1 < args.Length) timeoutMs = int.Parse(args[++i]);
        }
        if (count < 1) count = 1;
        if (concurrency <= 0) concurrency = Math.Clamp(Environment.ProcessorCount * 32, 64, 512);
        concurrency = Math.Min(concurrency, count);

        var serverListener = new EventBasedNetListener();
        var server = new NetManager(serverListener) { AutoRecycle = true, UpdateTime = 15 };
        serverListener.ConnectionRequestEvent += req => req.Accept();
        if (port <= 0) { if (!server.Start()) return 1; port = server.LocalPort; }
        else if (!server.Start(port)) return 1;
        Console.WriteLine($"[{DateTime.UtcNow:O}] [self-test] STAGE self_host_listen: port={port} count={count}");
        using var cts = new CancellationTokenSource();
        var hostLoop = Task.Run(() => { while (!cts.Token.IsCancellationRequested) { server.PollEvents(); Thread.Sleep(2); } });

        bool pass;
        if (count == 1)
        {
            var result = LiteNetProbe.Run("127.0.0.1", port, "", timeoutMs, 99, Console.WriteLine);
            pass = result.Pass && (result.Connected || result.Stages.Contains("litenet_peer_connected"));
        }
        else
        {
            var summary = LoadRunner.Run("127.0.0.1", port, "", Math.Max(timeoutMs, 6000), count, concurrency,
                rampMs: Math.Min(2000, count), quiet: true);
            Console.WriteLine(summary.ToReport());
            double connectRate = summary.Total == 0 ? 0 : (double)summary.Connected / summary.Total;
            pass = summary.PassRate + 1e-9 >= minPassRate && connectRate + 1e-9 >= Math.Min(minPassRate, 0.90);
        }
        cts.Cancel();
        try { hostLoop.Wait(1000); } catch { /* ignore */ }
        server.Stop();
        if (!string.IsNullOrEmpty(logPath))
            File.WriteAllText(logPath, $"self-test pass={pass} count={count}\n");
        Console.WriteLine(pass ? $"PASS: self-test count={count}" : "FAIL: self-test");
        return pass ? 0 : 1;
    }
}
