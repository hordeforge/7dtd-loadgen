namespace SevenDTD.LoadGen;

/// <summary>
/// Bot behaviour loops over real NetPackages after join.
/// Default: walk endlessly until world/server death or lifetime expires (no client self-kill).
/// Modes: wander, mixed, chatty, combat, patrol, chaos, demolition, bait, kite, traverse.
/// </summary>
public static class ActionLoop
{
    // 7DTD player run speed is ~5.7 m/s; a small margin keeps moves server-legal
    // and matched to the chunk streamer so a roaming bot loads chunks like a
    // real client instead of outrunning them.
    const float MaxRunSpeedMps = 6.0f;

    /// <summary>Default per-life dynamite cap; selecting Demolition while the cap
    /// is still this default auto-raises it to <see cref="DemolitionMaxDynamitePerLife"/>.</summary>
    public const int DefaultMaxDynamitePerLife = 3;

    /// <summary>Raised dynamite cap for Demolition mode (terrain destruction load).</summary>
    public const int DemolitionMaxDynamitePerLife = 200;

    public enum BotMode
    {
        /// <summary>Walk one heading + occasional jumps until world death.</summary>
        Wander = 0,
        /// <summary>Walk/jump/turn/crouch mix until world death (or client death if requested).</summary>
        Mixed = 1,
        /// <summary>Walk + frequent global chat.</summary>
        Chatty = 2,
        /// <summary>Walk + aim + crouch + punch-self damage spikes.</summary>
        Combat = 3,
        /// <summary>Square/patrol path with 90° turns.</summary>
        Patrol = 4,
        /// <summary>All live actions (no LookAt wire).</summary>
        Chaos = 5,
        /// <summary>Walk + heavy repeated dynamite (terrain destruction load).</summary>
        Demolition = 6,
        /// <summary>Near-stationary: tiny shuffle only. Isolates AI/combat cost from chunk churn.</summary>
        Bait = 7,
        /// <summary>Slow continuous arc inside the leash: chasing zombies must repath every
        /// tick, maximizing A* pathfinding churn (AstarVoxelGrid.InitScan alloc hotspot).</summary>
        Kite = 8,
        /// <summary>Long straight-line roam, no leash: continuously streams fresh chunks +
        /// tile entities (chunk-bandwidth + TileEntity.InstantiateFromRead churn).</summary>
        Traverse = 9,
    }

    public enum DeathMethod
    {
        /// <summary>Do not self-kill; wait for zombies/rad/water/server death or timeout.</summary>
        None = 0,
        Drown = 1,
        Suicide = 2,
        Killed = 3,
        Random = 4,
    }

    public enum ActionKind
    {
        Walk,
        Jump,
        Crouch,
        Aim,
        Turn,
        Strafe,
        Look,
        Chat,
        BreakBlocks,
        Dynamite,
        AttackSelf,
        Drown,
        Suicide,
        Killed,
    }

    public enum DeathCause
    {
        None = 0,
        DrownFatal,
        Suicide,
        KilledExternal,
        SuicideFallback,
        WorldDeath,
        ServerDisconnect,
        TimeoutAlive,
    }

    /// <summary>Per-run action counters; loop control + ACTION_SUMMARY evidence.</summary>
    sealed class Stats
    {
        public int Walks { get; set; }
        public int Jumps { get; set; }
        public int Crouches { get; set; }
        public int Aims { get; set; }
        public int Turns { get; set; }
        public int Strafes { get; set; }
        public int Looks { get; set; }
        public int Chats { get; set; }
        public int BreakBlocks { get; set; }
        public int Dynamite { get; set; }
        public int Attacks { get; set; }
        public int Drowns { get; set; }
        public int Suicides { get; set; }
        public int Killed { get; set; }
        public bool Died { get; set; }
        public DeathCause Cause { get; set; } = DeathCause.None;
    }

    public sealed class Options
    {
        /// <summary>Live steps to take. 0 or negative = endless until death/stop/lifetime.</summary>
        public int ActionCount { get; set; } = 0;
        public int Seed { get; set; } = 42;
        public BotMode Mode { get; set; } = BotMode.Wander;
        public DeathMethod Death { get; set; } = DeathMethod.None;
        public int PaceMs { get; set; } = -1; // -1 = mode default
        public string? ChatPrefix { get; set; }
        /// <summary>Total bots in this run; used to throttle chat at scale.</summary>
        public int CohortSize { get; set; } = 1;
        /// <summary>Wall-clock cap for the action loop (0 = no extra cap beyond ShouldStop).</summary>
        public int MaxLifetimeMs { get; set; } = 0;
        /// <summary>Dynamite cap per life (Demolition mode raises this).</summary>
        public int MaxDynamitePerLife { get; set; } = DefaultMaxDynamitePerLife;

        /// <summary>Returns current LiteNetLib RTT in ms, or -1 when unknown.</summary>
        public Func<int>? PingProbe { get; set; }
        /// <summary>Must poll LiteNetLib + drain inbox so long sessions stay connected.</summary>
        public Action? Poll { get; set; }
        /// <summary>True when client should stop (disconnect, world death, fail).</summary>
        public Func<bool>? ShouldStop { get; set; }
        public Action<string>? Log { get; set; }
        /// <summary>Optional cohort bench clock; counts action iterations inside the window.</summary>
        public BenchClock? Bench { get; set; }
    }

    public static void Run(
        JoinStateMachine sm,
        Func<byte[], bool> send,
        Options opt)
    {
        var stats = new Stats();
        var log = opt.Log;
        if (!sm.IsJoined && sm.Stage != JoinStage.SpawnedInWorld && sm.Stage != JoinStage.Joined)
        {
            log?.Invoke("ACTION skip: not joined");
            return;
        }

        ResolveIds(sm, out ushort posId, out ushort relId, out ushort flagsId,
            out ushort dmgId, out ushort chatId, out ushort explosionId);

        var rng = new Random(opt.Seed);
        // Reseed the (thread-static) pace-jitter RNG deterministically per bot so
        // runs are reproducible. opt.Seed already encodes ActionSeed + clientId +
        // life, giving each bot a distinct but fixed think-time sequence.
        _paceRng = new Random(opt.Seed ^ 0x5f3759df);
        int entityId = sm.EntityId > 0 ? sm.EntityId : 1;
        float x = sm.PosX;
        float originX = sm.PosX;
        float originZ = sm.PosZ;
        float y = sm.PosY > 1f ? sm.PosY : 72f;
        float z = sm.PosZ;
        // Hold surface height: long sessions must not sink via cumulative gravity.
        float surfaceY = y;
        float heading = (float)(rng.NextDouble() * Math.PI * 2);
        float yaw = heading * (180f / MathF.PI);
        float stepBase = 1.2f + (float)rng.NextDouble() * 0.9f;
        // Stay near spawn so telnet/AI zombies can reach the player (empty worlds have no POI attractors).
        float homeX = x;
        float homeZ = z;
        // Wide leash so bots can walk into POIs/sleeper volumes (height-test had none).
        // Traverse bots roam far to stream fresh chunks + tile entities continuously
        // (the chunk-bandwidth + TileEntity.InstantiateFromRead churn bottleneck).
        float leashRadius = opt.Mode == BotMode.Traverse
            ? 20000f
            : 180f + (float)rng.NextDouble() * 120f;
        float waterY = Math.Min(surfaceY - 8f, 16f);
        bool crouching = false;
        bool aiming = false;
        int patrolSteps = 0;
        int patrolLegLen = 6 + rng.Next(0, 6);
        int stepsUntilTurn = 8 + rng.Next(0, 16);

        bool endless = opt.ActionCount <= 0;
        bool clientDeath = opt.Death != DeathMethod.None;
        // Finite client-death runs keep a death window near the end; endless never self-kills.
        int n = endless
            ? int.MaxValue
            : Math.Max(opt.ActionCount, opt.Mode == BotMode.Wander ? 24 : 12);
        int deathAt = clientDeath && !endless ? DeathAtStep(n) : int.MaxValue;

        int paceMs = opt.PaceMs >= 0 ? opt.PaceMs : opt.Mode switch
        {
            BotMode.Wander => 50,
            BotMode.Demolition => 45,
            BotMode.Bait => 120,
            BotMode.Kite => 45, // steady tick-paced movement to force continuous repathing
            BotMode.Traverse => 45, // steady march to keep the chunk loader busy
            BotMode.Patrol => 50,
            BotMode.Chatty => 55,
            BotMode.Combat => 40,
            BotMode.Chaos => 35,
            _ => 45,
        };

        int maxChats = DefaultMaxChats(opt.Mode, opt.CohortSize);
        string chatPrefix = opt.ChatPrefix ?? $"bot{entityId}";
        // Per-life packages-sent delta: sm.PackagesSent is the session total, so
        // capture the entry value and report the difference in ACTION_SUMMARY.
        int sentBefore = sm.PackagesSent;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        log?.Invoke(
            $"ACTION start mode={opt.Mode} death={opt.Death} n={(endless ? "endless" : n.ToString())} " +
            $"entity={entityId} paceMs={paceMs} maxChats={maxChats} cohort={opt.CohortSize} " +
            $"lifetimeMs={(opt.MaxLifetimeMs > 0 ? opt.MaxLifetimeMs : -1)} surfaceY={surfaceY:0.#}");

        for (int i = 0; i < n; i++)
        {
            opt.Poll?.Invoke();

            if (stats.Died || sm.Died)
            {
                SyncDeathFromState(sm, stats);
                break;
            }
            if (opt.ShouldStop?.Invoke() == true)
            {
                if (!stats.Died && sm.Died)
                    SyncDeathFromState(sm, stats);
                else if (!stats.Died && sm.Stage == JoinStage.Disconnected)
                {
                    stats.Died = sm.Died;
                    if (string.IsNullOrEmpty(sm.DeathCause) || sm.DeathCause == "none")
                    {
                        sm.DeathCause = "server_disconnect";
                        stats.Cause = DeathCause.ServerDisconnect;
                    }
                    else
                        SyncDeathFromState(sm, stats);
                    log?.Invoke($"ACTION stop: disconnect cause={sm.DeathCause}");
                }
                else if (!stats.Died)
                    log?.Invoke($"ACTION stop: should_stop stage={sm.Stage} cause={sm.DeathCause}");
                break;
            }
            if (opt.MaxLifetimeMs > 0 && sw.ElapsedMilliseconds >= opt.MaxLifetimeMs)
            {
                if (!stats.Died && !sm.Died)
                {
                    stats.Cause = DeathCause.TimeoutAlive;
                    sm.DeathCause = "timeout_alive";
                    log?.Invoke(
                        $"ACTION timeout_alive after {sw.ElapsedMilliseconds}ms walks={stats.Walks} " +
                        $"pos=({x:0.#},{y:0.#},{z:0.#})");
                }
                break;
            }

            // Wander: occasional random heading so bots explore instead of walking a line forever.
            // Adopt a corrected ground height mid-loop if the receive path
            // learned the server-authoritative Y after this loop started.
            if (sm.GroundAdopted && Math.Abs(sm.PosY - surfaceY) > 1f)
            {
                surfaceY = sm.PosY;
                y = surfaceY;
            }
            // Bots self-report Y from their spawn height; wandering far onto
            // different terrain embeds them and breaks server-side spawn-point
            // search near the player. Stay within a sane leash of the spawn.
            // Traverse roams freely to stream fresh chunks; GroundAdopted keeps
            // its Y on the server terrain (GameJoinClient receive path) so it
            // does not embed.
            float leashDx = x - originX, leashDz = z - originZ;
            if (opt.Mode != BotMode.Traverse && leashDx * leashDx + leashDz * leashDz > 45f * 45f)
            {
                heading = MathF.Atan2(originZ - z, originX - x);
                yaw = heading * (180f / MathF.PI);
                stepsUntilTurn = 20 + rng.Next(0, 20);
            }
            if ((opt.Mode is BotMode.Wander or BotMode.Mixed or BotMode.Demolition) && stepsUntilTurn-- <= 0)
            {
                heading += (float)((rng.NextDouble() - 0.5) * Math.PI); // ±90°
                yaw = heading * (180f / MathF.PI);
                stepsUntilTurn = 12 + rng.Next(0, 28);
                stats.Turns++;
                sm.TurnActions++;
                if (stats.Turns <= 5 || stats.Turns % 20 == 0)
                    log?.Invoke($"ACTION turn#{stats.Turns} entity={entityId} yaw={yaw:0.#}");
            }

            if (opt.PingProbe != null && i % 25 == 0)
            {
                int ping = opt.PingProbe();
                if (ping >= 0) PingStats.Record(ping);
            }
            ActionKind kind = PickAction(opt.Mode, rng, i, n, deathAt, opt.Death, stats.Chats, maxChats);
            // Bench mode counts one action iteration per pick, inside the window.
            opt.Bench?.OnAction();
            // Every joined client gets a bounded chance to exercise destructive world load.
            bool demolition = opt.Mode == BotMode.Demolition;
            bool wantDynamite = demolition
                ? i >= 4 && i % 6 == 0
                : i >= 8 && (i == 12 || rng.NextDouble() < 0.025);
            if (explosionId != 0 && stats.Dynamite < opt.MaxDynamitePerLife
                && wantDynamite)
                kind = ActionKind.Dynamite;

            switch (kind)
            {
                case ActionKind.Jump:
                    y = surfaceY + 1.1f;
                    SendFlags(send, sm, flagsId, entityId,
                        (ushort)(PackageCodec.FlagJumping | PackageCodec.FlagSpawned
                            | (crouching ? PackageCodec.FlagCrouching : 0)
                            | (aiming ? PackageCodec.FlagAimingGun : 0)),
                        () =>
                        {
                            stats.Jumps++;
                            sm.JumpActions++;
                            if (stats.Jumps <= 5 || stats.Jumps % 25 == 0)
                                log?.Invoke($"ACTION jump#{stats.Jumps} entity={entityId} y={y:0.##}");
                        });
                    Pace(paceMs, opt.Poll, opt.ShouldStop);
                    y = surfaceY;
                    goto case ActionKind.Walk;

                case ActionKind.Crouch:
                    crouching = !crouching;
                    aiming = false;
                    SendFlags(send, sm, flagsId, entityId,
                        (ushort)(PackageCodec.FlagSpawned
                            | (crouching ? PackageCodec.FlagCrouching : 0)),
                        () =>
                        {
                            stats.Crouches++;
                            sm.CrouchActions++;
                            if (stats.Crouches <= 5 || stats.Crouches % 20 == 0)
                                log?.Invoke($"ACTION crouch#{stats.Crouches} entity={entityId} on={crouching}");
                        });
                    Pace(paceMs, opt.Poll, opt.ShouldStop);
                    break;

                case ActionKind.Aim:
                    aiming = !aiming;
                    SendFlags(send, sm, flagsId, entityId,
                        (ushort)(PackageCodec.FlagSpawned
                            | (aiming ? PackageCodec.FlagAimingGun : 0)
                            | (crouching ? PackageCodec.FlagCrouching : 0)),
                        () =>
                        {
                            stats.Aims++;
                            sm.AimActions++;
                            if (stats.Aims <= 5 || stats.Aims % 20 == 0)
                                log?.Invoke($"ACTION aim#{stats.Aims} entity={entityId} on={aiming}");
                        });
                    Pace(paceMs, opt.Poll, opt.ShouldStop);
                    break;

                case ActionKind.BreakBlocks:
                    SendFlags(send, sm, flagsId, entityId,
                        (ushort)(PackageCodec.FlagSpawned | PackageCodec.FlagBreakingBlocks
                            | (crouching ? PackageCodec.FlagCrouching : 0)),
                        () =>
                        {
                            stats.BreakBlocks++;
                            sm.BreakBlockActions++;
                            if (stats.BreakBlocks <= 3 || stats.BreakBlocks % 10 == 0)
                                log?.Invoke($"ACTION break#{stats.BreakBlocks} entity={entityId}");
                        });
                    Pace(paceMs, opt.Poll, opt.ShouldStop);
                    break;

                case ActionKind.Dynamite:
                    {
                        float distance = 8f + (float)rng.NextDouble() * 10f;
                        float tx = x + MathF.Cos(heading) * distance;
                        float tz = z + MathF.Sin(heading) * distance;
                        var pkt = PackageCodec.BuildDynamiteExplosion(
                            explosionId, entityId, tx, surfaceY, tz);
                        if (send(pkt))
                        {
                            stats.Dynamite++;
                            sm.PackagesSent++;
                            log?.Invoke($"ACTION dynamite#{stats.Dynamite} entity={entityId} target=({tx:0.#},{surfaceY:0.#},{tz:0.#}) fuse=4s");
                        }
                        Pace(paceMs, opt.Poll, opt.ShouldStop);
                        break;
                    }

                case ActionKind.Turn:
                    {
                        float delta = (float)((rng.NextDouble() - 0.5) * Math.PI);
                        if (opt.Mode == BotMode.Patrol)
                            delta = MathF.PI / 2f;
                        heading += delta;
                        yaw = heading * (180f / MathF.PI);
                        stats.Turns++;
                        sm.TurnActions++;
                        if (stats.Turns <= 5 || stats.Turns % 20 == 0)
                            log?.Invoke($"ACTION turn#{stats.Turns} entity={entityId} yaw={yaw:0.#}");
                        Move(send, sm, posId, relId, entityId, ref x, ref y, ref z, surfaceY,
                            0f, 0f, yaw, crouching, () => { });
                        Pace(paceMs, opt.Poll, opt.ShouldStop);
                        break;
                    }

                case ActionKind.Strafe:
                    {
                        float side = rng.Next(0, 2) == 0 ? 1f : -1f;
                        float dx = MathF.Cos(heading + MathF.PI / 2f) * stepBase * 0.7f * side;
                        float dz = MathF.Sin(heading + MathF.PI / 2f) * stepBase * 0.7f * side;
                        Move(send, sm, posId, relId, entityId, ref x, ref y, ref z, surfaceY,
                            dx, dz, yaw, crouching, () =>
                            {
                                stats.Strafes++;
                                sm.StrafeActions++;
                                if (stats.Strafes <= 3 || stats.Strafes % 25 == 0)
                                    log?.Invoke($"ACTION strafe#{stats.Strafes} entity={entityId}");
                            });
                        Pace(paceMs, opt.Poll, opt.ShouldStop);
                        break;
                    }

                case ActionKind.Look:
                    stats.Looks++;
                    sm.LookActions++;
                    Pace(paceMs, opt.Poll, opt.ShouldStop);
                    break;

                case ActionKind.Chat:
                    {
                        if (chatId == 0 || stats.Chats >= maxChats)
                            goto case ActionKind.Walk;
                        string msg = $"{chatPrefix}: hello #{stats.Chats + 1} @({x:0},{z:0}) mode={opt.Mode}";
                        var pkt = PackageCodec.BuildSimpleChat(chatId, msg);
                        if (send(pkt))
                        {
                            stats.Chats++;
                            sm.ChatActions++;
                            sm.PackagesSent++;
                            log?.Invoke($"ACTION chat#{stats.Chats} entity={entityId} msg={msg}");
                        }
                        Pace(paceMs, opt.Poll, opt.ShouldStop);
                        break;
                    }

                case ActionKind.AttackSelf:
                    {
                        var pkt = PackageCodec.BuildDamageEntity(
                            dmgId, entityId,
                            PackageCodec.DamageSourceExternal,
                            PackageCodec.DamageTypeBashing,
                            strength: 5, fatal: false, attackerEntityId: entityId);
                        if (send(pkt))
                        {
                            stats.Attacks++;
                            sm.AttackActions++;
                            sm.PackagesSent++;
                            if (stats.Attacks <= 3 || stats.Attacks % 10 == 0)
                                log?.Invoke($"ACTION attack#{stats.Attacks} entity={entityId}");
                        }
                        Pace(paceMs, opt.Poll, opt.ShouldStop);
                        break;
                    }

                case ActionKind.Walk:
                    {
                        float step = stepBase;
                        if (opt.Mode == BotMode.Bait)
                        {
                            step = 0.15f; // shuffle in place; stay inside one chunk
                            heading += (float)((rng.NextDouble() - 0.5) * Math.PI);
                        }
                        if (opt.Mode == BotMode.Kite)
                        {
                            step = 0.6f; // slow steady drift, stays within leash
                            heading += 0.35f; // constant arc: chasers repath every tick
                            yaw = heading * (180f / MathF.PI);
                        }
                        if (opt.Mode == BotMode.Traverse)
                        {
                            // Mostly straight march; occasional small drift so a wall/hill
                            // does not stall forward progress (and chunk streaming) forever.
                            if (rng.NextDouble() < 0.06)
                            {
                                heading += (float)((rng.NextDouble() - 0.5) * (Math.PI / 3));
                                yaw = heading * (180f / MathF.PI);
                            }
                        }
                        if (opt.Mode == BotMode.Patrol)
                        {
                            patrolSteps++;
                            if (patrolSteps >= patrolLegLen)
                            {
                                patrolSteps = 0;
                                heading += MathF.PI / 2f;
                                yaw = heading * (180f / MathF.PI);
                                patrolLegLen = 6 + rng.Next(0, 6);
                            }
                        }
                        if (opt.Mode == BotMode.Chaos && rng.NextDouble() < 0.15)
                            step *= 1.6f;

                        // Cap per-move distance to a real run speed. A bot moving far
                        // faster than a player outruns the server's chunk streamer, so
                        // it travels through not-yet-sent chunks and paradoxically
                        // measures LOW chunk bandwidth. Realistic speed makes the
                        // server stream continuously to keep up (faithful client load).
                        // Only when we have a real per-move interval: at pace<=0 there
                        // is no think-time to convert to a distance, and a 0 cap would
                        // freeze the bot.
                        if (paceMs > 0)
                        {
                            float maxStep = MaxRunSpeedMps * (paceMs / 1000f);
                            if (step > maxStep) step = maxStep;
                        }

                        // Leash: turn home if we drift too far from spawn (zombie reachability).
                        float distHome = MathF.Sqrt((x - homeX) * (x - homeX) + (z - homeZ) * (z - homeZ));
                        if (distHome > leashRadius)
                        {
                            heading = MathF.Atan2(homeZ - z, homeX - x);
                            yaw = heading * (180f / MathF.PI);
                            stepsUntilTurn = 10 + rng.Next(0, 10);
                        }

                        float dx = MathF.Cos(heading) * step;
                        float dz = MathF.Sin(heading) * step;
                        Move(send, sm, posId, relId, entityId, ref x, ref y, ref z, surfaceY,
                            dx, dz, yaw, crouching, () =>
                            {
                                stats.Walks++;
                                sm.WalkActions++;
                                if (stats.Walks <= 5 || stats.Walks % 50 == 0)
                                    log?.Invoke(
                                        $"ACTION walk#{stats.Walks} entity={entityId} " +
                                        $"-> ({x:0.##},{y:0.##},{z:0.##}) hdg={yaw:0.#}");
                            });
                        Pace(paceMs, opt.Poll, opt.ShouldStop);
                        break;
                    }

                case ActionKind.Drown:
                    DoDrown(send, sm, stats, posId, dmgId, entityId, ref x, ref y, ref z, yaw, waterY, log);
                    break;
                case ActionKind.Suicide:
                    DoSuicide(send, sm, stats, dmgId, entityId, log, fallback: false);
                    break;
                case ActionKind.Killed:
                    DoKilled(send, sm, stats, dmgId, entityId, log);
                    break;
            }
        }

        // Only client-forced death paths end with suicide fallback.
        if (clientDeath && !stats.Died && !sm.Died && !endless)
            DoSuicide(send, sm, stats, dmgId, entityId, log, fallback: true);

        if (!stats.Died && sm.Died)
            SyncDeathFromState(sm, stats);

        log?.Invoke(
            $"ACTION_SUMMARY mode={opt.Mode} walks={stats.Walks} jumps={stats.Jumps} crouch={stats.Crouches} " +
            $"aim={stats.Aims} turn={stats.Turns} strafe={stats.Strafes} look={stats.Looks} chat={stats.Chats} " +
            $"break={stats.BreakBlocks} dynamite={stats.Dynamite} attack={stats.Attacks} drowns={stats.Drowns} suicides={stats.Suicides} " +
            $"killed={stats.Killed} died={stats.Died} cause={stats.Cause} " +
            $"deathCause={sm.DeathCause} elapsedMs={sw.ElapsedMilliseconds} sent={sm.PackagesSent - sentBefore}");
    }

    static void SyncDeathFromState(JoinStateMachine sm, Stats stats)
    {
        stats.Died = sm.Died || stats.Died;
        if (stats.Cause != DeathCause.None) return;
        stats.Cause = sm.DeathCause switch
        {
            "drown_fatal" => DeathCause.DrownFatal,
            "suicide" => DeathCause.Suicide,
            "suicide_fallback" => DeathCause.SuicideFallback,
            "killed_external" => DeathCause.KilledExternal,
            "world_death" => DeathCause.WorldDeath,
            "server_disconnect" => DeathCause.ServerDisconnect,
            "timeout_alive" => DeathCause.TimeoutAlive,
            _ => sm.Died ? DeathCause.WorldDeath : DeathCause.None,
        };
    }

    static void ResolveIds(
        JoinStateMachine sm,
        out ushort posId, out ushort relId, out ushort flagsId,
        out ushort dmgId, out ushort chatId, out ushort explosionId)
    {
        if (!sm.TryGetPackageId("NetPackageEntityPosAndRot", out posId)) posId = 0;
        if (!sm.TryGetPackageId("NetPackageEntityRelPosAndRot", out relId)) relId = posId;
        if (!sm.TryGetPackageId("NetPackageEntityAliveFlags", out flagsId)) flagsId = posId;
        if (!sm.TryGetPackageId("NetPackageDamageEntity", out dmgId)) dmgId = 0;
        if (!sm.TryGetPackageId("NetPackageSimpleChat", out chatId)
            && !sm.TryGetPackageId("NetPackageChat", out chatId))
            chatId = 0;
        if (!sm.TryGetPackageId("NetPackageExplosionInitiate", out explosionId)) explosionId = 0;
    }

    static int DefaultMaxChats(BotMode mode, int cohort)
    {
        if (cohort >= 100)
            return mode == BotMode.Chatty ? 1 : 0;
        if (cohort >= 20)
            return mode == BotMode.Chatty ? 3 : (mode == BotMode.Chaos ? 1 : 0);
        return mode switch
        {
            BotMode.Chatty => 12,
            BotMode.Chaos => 5,
            _ => 2,
        };
    }

    static ActionKind PickAction(
        BotMode mode, Random rng, int i, int n, int deathAt, DeathMethod deathPref,
        int chatsSoFar, int maxChats)
    {
        if (deathPref != DeathMethod.None && i >= deathAt && deathAt < int.MaxValue)
            return PickDeath(rng, deathPref, i - deathAt);

        ActionKind k = mode switch
        {
            BotMode.Wander => rng.NextDouble() < 0.12 ? ActionKind.Jump : ActionKind.Walk,
            BotMode.Mixed => PickMixedLive(rng),
            BotMode.Chatty => PickChatty(rng),
            BotMode.Combat => PickCombat(rng),
            BotMode.Patrol => rng.NextDouble() < 0.12 ? ActionKind.Jump : ActionKind.Walk,
            BotMode.Chaos => PickChaos(rng),
            BotMode.Demolition => rng.NextDouble() < 0.08 ? ActionKind.Jump : ActionKind.Walk,
            BotMode.Kite => ActionKind.Walk, // steady movement so chasers never finish a path
            BotMode.Traverse => rng.NextDouble() < 0.05 ? ActionKind.Jump : ActionKind.Walk,
            BotMode.Bait => rng.NextDouble() switch
            {
                < 0.15 => ActionKind.Crouch,
                < 0.30 => ActionKind.Aim,
                < 0.45 => ActionKind.Walk, // tiny shuffle keeps the connection warm
                _ => ActionKind.Look,
            },
            _ => ActionKind.Walk,
        };
        if (k == ActionKind.Chat && chatsSoFar >= maxChats)
            return ActionKind.Walk;
        return k;
    }

    static ActionKind PickDeath(Random rng, DeathMethod pref, int step)
    {
        DeathMethod m = pref == DeathMethod.Random
            ? (DeathMethod)rng.Next(1, 4) // Drown..Killed
            : pref;
        if (m == DeathMethod.None)
            return ActionKind.Walk;
        if (step > 0)
            m = (DeathMethod)(1 + (((int)m - 1 + step) % 3));
        return m switch
        {
            DeathMethod.Suicide => ActionKind.Suicide,
            DeathMethod.Killed => ActionKind.Killed,
            _ => ActionKind.Drown,
        };
    }

    static ActionKind PickMixedLive(Random rng)
    {
        double r = rng.NextDouble();
        if (r < 0.55) return ActionKind.Walk;
        if (r < 0.70) return ActionKind.Jump;
        if (r < 0.82) return ActionKind.Turn;
        if (r < 0.92) return ActionKind.Strafe;
        return ActionKind.Crouch;
    }

    static ActionKind PickChatty(Random rng)
    {
        double r = rng.NextDouble();
        if (r < 0.45) return ActionKind.Walk;
        if (r < 0.70) return ActionKind.Chat;
        if (r < 0.82) return ActionKind.Jump;
        if (r < 0.92) return ActionKind.Turn;
        return ActionKind.Strafe;
    }

    static ActionKind PickCombat(Random rng)
    {
        double r = rng.NextDouble();
        if (r < 0.40) return ActionKind.Walk;
        if (r < 0.55) return ActionKind.Strafe;
        if (r < 0.68) return ActionKind.Aim;
        if (r < 0.78) return ActionKind.Crouch;
        if (r < 0.88) return ActionKind.AttackSelf;
        if (r < 0.95) return ActionKind.Jump;
        return ActionKind.BreakBlocks;
    }

    static ActionKind PickChaos(Random rng)
    {
        double r = rng.NextDouble();
        if (r < 0.32) return ActionKind.Walk;
        if (r < 0.42) return ActionKind.Jump;
        if (r < 0.52) return ActionKind.Strafe;
        if (r < 0.60) return ActionKind.Turn;
        if (r < 0.68) return ActionKind.Crouch;
        if (r < 0.76) return ActionKind.Aim;
        if (r < 0.86) return ActionKind.Chat;
        if (r < 0.93) return ActionKind.AttackSelf;
        return ActionKind.BreakBlocks;
    }

    // A real client emits mostly relative position deltas and resyncs the
    // authoritative absolute position only occasionally. Send the absolute
    // package every Nth move as a keyframe instead of on every move (which
    // doubled the movement packet rate the server had to process).
    const long AbsKeyframeInterval = 4;

    static void Move(
        Func<byte[], bool> send, JoinStateMachine sm,
        ushort posId, ushort relId, int entityId,
        ref float x, ref float y, ref float z, float surfaceY,
        float dx, float dz, float yaw, bool crouching, Action onOk)
    {
        // No position package id from the server mapping -> we cannot address a
        // move packet; skip rather than emit id=0 (which the server would
        // misroute to its PackageIds parser). Matches the chat-action guard.
        if (posId == 0) return;
        float prevY = y;
        x += dx;
        z += dz;
        y = surfaceY;
        short sdx = (short)Math.Clamp((int)Math.Round(dx * 32), short.MinValue, short.MaxValue);
        // Carry the vertical delta in the relative package so Y stays in sync on
        // every tick, not only on the periodic absolute keyframe below.
        short sdy = (short)Math.Clamp((int)Math.Round((y - prevY) * 32), short.MinValue, short.MaxValue);
        short sdz = (short)Math.Clamp((int)Math.Round(dz * 32), short.MinValue, short.MaxValue);
        short sYaw = PackageCodec.PackAngle256(yaw);

        var rel = PackageCodec.BuildEntityRelPosAndRot(
            relId, entityId, sdx, sdy, sdz, rotX: 0, rotY: sYaw, rotZ: 0,
            onGround: !crouching, updateSteps: 1);

        bool relOk = send(rel);
        if (relOk) { sm.PackagesSent++; }

        // Absolute keyframe: periodically resync the authoritative position so
        // any drift in the accumulated relative deltas is corrected.
        bool absOk = false;
        if (++sm.MoveTicks % AbsKeyframeInterval == 0)
        {
            var abs = PackageCodec.BuildEntityPosAndRot(
                posId, entityId, x, y, z, 0f, yaw, 0f, onGround: !crouching);
            absOk = send(abs);
            if (absOk) { sm.PackagesSent++; }
        }

        // Advance local state whenever a position package actually went out
        // (rel-only ticks included) so Y/state tracking never stalls between
        // keyframes. Count rel and abs separately so a failed abs send does not
        // over-count PackagesSent.
        if (relOk || absOk)
        {
            sm.PosX = x;
            sm.PosY = y;
            sm.PosZ = z;
            onOk();
        }
    }

    static void SendFlags(
        Func<byte[], bool> send, JoinStateMachine sm,
        ushort flagsId, int entityId, ushort flags, Action onOk)
    {
        if (flagsId == 0) return;  // unmapped: skip rather than send id=0
        var pkt = PackageCodec.BuildEntityAliveFlags(flagsId, entityId, flags);
        if (send(pkt))
        {
            sm.PackagesSent++;
            onOk();
        }
    }

    static void DoDrown(
        Func<byte[], bool> send, JoinStateMachine sm, Stats stats,
        ushort posId, ushort dmgId, int entityId,
        ref float x, ref float y, ref float z, float yaw, float waterY, Action<string>? log)
    {
        if (dmgId == 0) return;  // no damage package id: cannot enact death
        y = waterY;
        var abs = PackageCodec.BuildEntityPosAndRot(
            posId, entityId, x, y, z, 0f, yaw, 0f, onGround: false);
        var dmg = PackageCodec.BuildDrown(dmgId, entityId);
        if (send(abs)) { sm.PackagesSent++; }
        if (send(dmg))
        {
            stats.Drowns++;
            sm.DrownActions++;
            sm.PackagesSent++;
            sm.PosY = y;
            log?.Invoke($"ACTION drown#{stats.Drowns} entity={entityId} y={y:0.##}");
        }
        var fatal = PackageCodec.BuildDamageEntity(
            dmgId, entityId,
            PackageCodec.DamageSourceInternal,
            PackageCodec.DamageTypeSuffocation,
            9999, fatal: true);
        if (send(fatal))
        {
            sm.PackagesSent++;
            stats.Died = true;
            sm.Died = true;
            stats.Cause = DeathCause.DrownFatal;
            sm.DeathCause = "drown_fatal";
            log?.Invoke($"ACTION drown_fatal entity={entityId}");
        }
    }

    static void DoSuicide(
        Func<byte[], bool> send, JoinStateMachine sm, Stats stats,
        ushort dmgId, int entityId, Action<string>? log, bool fallback)
    {
        if (dmgId == 0) return;
        var pkt = PackageCodec.BuildSuicide(dmgId, entityId);
        if (!send(pkt)) return;
        stats.Suicides++;
        sm.SuicideActions++;
        sm.PackagesSent++;
        stats.Died = true;
        sm.Died = true;
        stats.Cause = fallback ? DeathCause.SuicideFallback : DeathCause.Suicide;
        sm.DeathCause = fallback ? "suicide_fallback" : "suicide";
        log?.Invoke($"ACTION suicide entity={entityId} fallback={fallback}");
    }

    static void DoKilled(
        Func<byte[], bool> send, JoinStateMachine sm, Stats stats,
        ushort dmgId, int entityId, Action<string>? log)
    {
        if (dmgId == 0) return;
        var pkt = PackageCodec.BuildExternalKill(dmgId, entityId, attackerId: -2);
        if (!send(pkt)) return;
        stats.Killed++;
        sm.KilledActions++;
        sm.PackagesSent++;
        stats.Died = true;
        sm.Died = true;
        stats.Cause = DeathCause.KilledExternal;
        sm.DeathCause = "killed_external";
        log?.Invoke($"ACTION killed entity={entityId}");
    }

    [ThreadStatic] static Random? _paceRng;

    /// <summary>Step where a client-forced death lands: 80% into the planned
    /// steps, but never before step n-2 (for n >= 10 the n-2 bound wins, so
    /// realistic runs die two steps from the end). The multiply widens to long
    /// first: --actions parses up to int.MaxValue unclamped, and above
    /// 536870911 the int product n*4 wraps negative. Today the Max masks that
    /// wrap; keeping the term correct on its own means an edit to either bound
    /// cannot silently resurrect it.</summary>
    internal static int DeathAtStep(int plannedSteps) =>
        Math.Max(plannedSteps - 2, (int)((long)plannedSteps * 4 / 5));

    /// <summary>Apply the ±20% think-time jitter. Saturates instead of wrapping:
    /// a huge --pace-ms makes the double product exceed int range, and .NET's
    /// out-of-range double→int cast yields int.MinValue - a negative pace that
    /// would skip the sleep loop below and silently turn the most-paced bot into
    /// an unpaced spammer.</summary>
    internal static int JitteredPaceMs(int paceMs, Random rng)
    {
        long jittered = (long)(paceMs * (0.8 + rng.NextDouble() * 0.4));
        return (int)Math.Min(jittered, int.MaxValue);
    }

    static void Pace(int paceMs, Action? poll, Func<bool>? shouldStop = null)
    {
        if (paceMs <= 0)
        {
            poll?.Invoke();
            return;
        }
        // +/-20% think-time jitter so a cohort does not act in lockstep and
        // create synthetic synchronized load spikes (real players are async).
        _paceRng ??= new Random(0x5f3759df);  // deterministic fallback if Run() did not seed
        paceMs = JitteredPaceMs(paceMs, _paceRng);
        // Slice sleep so LiteNet keeps ticking on long-lived sessions.
        int left = paceMs;
        while (left > 0)
        {
            int slice = Math.Min(left, 20);
            Thread.Sleep(slice);
            left -= slice;
            // Stop before polling: during process shutdown the caller must quit
            // within one slice instead of driving PollEvents/Send for the rest
            // of a long pace interval while the sweep tears managers down.
            if (shouldStop?.Invoke() == true) return;
            poll?.Invoke();
        }
    }

    public static bool TryParseMode(string s, out BotMode mode)
    {
        mode = BotMode.Wander;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return Enum.TryParse(s.Trim(), ignoreCase: true, out mode);
    }

    public static bool TryParseDeath(string s, out DeathMethod death)
    {
        death = DeathMethod.None;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().ToLowerInvariant();
        if (s is "none" or "natural" or "world" or "server" or "live") { death = DeathMethod.None; return true; }
        if (s is "drown" or "drown_fatal" or "water") { death = DeathMethod.Drown; return true; }
        if (s is "suicide" or "killself") { death = DeathMethod.Suicide; return true; }
        if (s is "killed" or "kill" or "external") { death = DeathMethod.Killed; return true; }
        if (s is "random" or "any") { death = DeathMethod.Random; return true; }
        return Enum.TryParse(s, ignoreCase: true, out death);
    }
}
