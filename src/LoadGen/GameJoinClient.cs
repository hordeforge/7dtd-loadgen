using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SevenDTD.LoadGen;

/// <summary>
/// Full 7DTD join path: LiteNetLib connect → challenge echo → PackageIds → PlayerLogin
/// → spawn → random walk/jump/drown/suicide/killed actions.
/// Binds unique 127.x.x.x when LocalBindIp set (bypasses dedicated 500ms/IP rate limit).
/// </summary>
public sealed class GameJoinClient
{
    /// <summary>Live LiteNetLib managers, for graceful disconnect on process exit
    /// (hard kills otherwise leave server-side player ghosts that deny later joins).</summary>
    // Dictionary (not a bag) so a finished client removes its own manager: a bag
    // grows unbounded across rejoins, leaking sockets + memory and letting
    // DisconnectAllActive double-stop already-stopped managers.
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<LiteNetLib.NetManager, byte> ActiveNets = new();

    public static void DisconnectAllActive()
    {
        foreach (var n in ActiveNets.Keys) { try { n.DisconnectAll(); n.PollEvents(); } catch { } }
        System.Threading.Thread.Sleep(200);
        foreach (var n in ActiveNets.Keys) { try { n.Stop(); } catch { } }
    }

    /// <summary>Stop a manager and drop it from the live set (idempotent). Every
    /// Run() exit path must call this so no UDP socket or manager leaks.</summary>
    static void StopNet(LiteNetLib.NetManager net)
    {
        ActiveNets.TryRemove(net, out _);
        try { net.Stop(); } catch { }
    }

    public JoinStateMachine State { get; } = new();

    public sealed class Options
    {
        public string Host { get; set; } = "127.0.0.1";
        // LiteNetLib listen port is ServerPort (26900); Steam may use +2 — try 26900 first.
        // Observed working LiteNet handshake on 26902 in this install.
        public int Port { get; set; } = 26902;
        public string Password { get; set; } = "";
        public string PlayerName { get; set; } = "REFake";
        /// <summary>Overall wall-clock budget (join + live). Default 1 hour for long walks.</summary>
        public int TimeoutMs { get; set; } = 3_600_000;
        /// <summary>Action steps after join. 0 = endless until world death / timeout.</summary>
        public int ActionCount { get; set; } = 0;
        public int ActionSeed { get; set; } = 42;
        public int ClientId { get; set; } = 1;
        public bool SkipActions { get; set; }
        /// <summary>Walk until world death (default true). Overridden by Mode.</summary>
        public bool WanderUntilDeath { get; set; } = true;
        /// <summary>Bot behaviour mode (wander/mixed/chatty/combat/patrol/chaos).</summary>
        public ActionLoop.BotMode Mode { get; set; } = ActionLoop.BotMode.Wander;
        /// <summary>Client self-kill method. Default None = wait for zombies/rad/water/server.</summary>
        public ActionLoop.DeathMethod Death { get; set; } = ActionLoop.DeathMethod.None;
        /// <summary>Optional pace override in ms (-1 = mode default).</summary>
        public int PaceMs { get; set; } = -1;

        /// <summary>Dynamite cap per life (Demolition mode raises this).</summary>
        public int MaxDynamitePerLife { get; set; } = 3;
        /// <summary>Total bots in this run (chat throttle).</summary>
        public int CohortSize { get; set; } = 1;
        /// <summary>After world death: request spawn and walk again (default true).</summary>
        public bool Respawn { get; set; } = true;
        /// <summary>Stop after this many deaths (0 = unlimited until overall timeout).</summary>
        public int MaxLives { get; set; } = 0;
        /// <summary>Wait after death before RequestToSpawnPlayer.</summary>
        public int RespawnDelayMs { get; set; } = 1500;
        /// <summary>Max wait for the server to confirm a respawn before giving up.
        /// Under heavy load the spawn-point search plus chunk load can exceed the
        /// old hardcoded 15s, so this is generous by default.</summary>
        public int RespawnTimeoutMs { get; set; } = 40000;
        /// <summary>Loopback bind e.g. 127.0.1.5 so server rate-limit is per-IP.</summary>
        public string? LocalBindIp { get; set; }
        /// <summary>Optional server-side provisioning hook invoked at the start of each life.</summary>
        public Action<int>? OnLifeStarted { get; set; }
        public Action<string>? Log { get; set; }
    }

    public int Run(Options opt)
    {
        void Log(string msg)
        {
            string line = $"[{DateTime.UtcNow:O}] [join#{opt.ClientId}] {msg}";
            opt.Log?.Invoke(line);
            State.Note(line);
        }

        string bindIp = string.IsNullOrWhiteSpace(opt.LocalBindIp) ? "0.0.0.0" : opt.LocalBindIp!;
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(bindIp == "0.0.0.0" ? "127.0.0.1" : bindIp), 0));
            udp.Client.ReceiveTimeout = 500;
            udp.Connect(opt.Host, opt.Port);
            State.Advance(JoinStage.UdpOpen, $"{opt.Host}:{opt.Port} bind={bindIp}");
            Log($"STAGE UdpOpen: ok bind={bindIp}");
        }
        catch (Exception ex)
        {
            State.Fail($"udp_open: {ex.Message}");
            Log($"FAIL udp: {ex.Message}");
            return 1;
        }

        var listener = new EventBasedNetListener();
        // DisconnectTimeout is "ms without peer traffic", NOT session length.
        // Using session TimeoutMs (e.g. 1h) is fine for silence tolerance, but a
        // sane floor/ceiling keeps half-open sockets from hanging forever.
        int peerIdleMs = Math.Clamp(opt.TimeoutMs, 30_000, 300_000);
        var net = new NetManager(listener)
        {
            AutoRecycle = true,
            DisconnectTimeout = peerIdleMs,
            PingInterval = 1000,
            UpdateTime = 15,
            UnsyncedEvents = false,
        };

        NetPeer? peer = null;
        ActiveNets[net] = 0;
        var sendQueue = new Queue<byte[]>();
        var inbox = new Queue<byte[]>();
        object gate = new();
        const int MaxInbox = 2_000;
        int verboseRecvLeft = 40; // throttle wire logs after join

        listener.PeerConnectedEvent += p =>
        {
            peer = p;
            State.Advance(JoinStage.LiteNetConnected, $"{p.Address}:{p.Port}");
            Log("STAGE LiteNetConnected");
        };
        listener.PeerDisconnectedEvent += (p, info) =>
        {
            Log($"STAGE Disconnected: {info.Reason}");
            if (!State.IsJoined)
                State.Fail($"disconnected: {info.Reason}");
            else
            {
                if (!State.Died && (State.DeathCause is "none" or null or ""))
                    State.DeathCause = "server_disconnect";
                State.Advance(JoinStage.Disconnected, info.Reason.ToString());
            }
        };
        listener.NetworkReceiveEvent += (p, reader, channel, method) =>
        {
            int n = reader.AvailableBytes;
            if (n <= 0) { reader.Recycle(); return; }
            var buf = new byte[n];
            reader.GetBytes(buf, n);
            reader.Recycle();
            lock (gate)
            {
                // Cap queue so long sessions / bursty chunk spam cannot OOM or stall pace loop.
                while (inbox.Count >= MaxInbox)
                    inbox.Dequeue();
                inbox.Enqueue(buf);
            }
        };

        // Bind per-client loopback IP so dedicated rate-limit (500ms/IP) and pending-login/IP do not serialize all 1000.
        bool started;
        if (!string.IsNullOrWhiteSpace(opt.LocalBindIp) && opt.LocalBindIp != "0.0.0.0")
        {
            var v4 = IPAddress.Parse(opt.LocalBindIp);
            started = net.Start(v4, IPAddress.IPv6Any, 0);
        }
        else
        {
            started = net.Start();
        }
        if (!started)
        {
            State.Fail("litenet_start");
            StopNet(net);
            return 1;
        }
        State.Advance(JoinStage.LiteNetStarted);
        Log($"STAGE LiteNetStarted bind={bindIp}");

        // Always put password string (even empty) — server GetString() vs serverPassword.
        var writer = new NetDataWriter();
        writer.Put(opt.Password ?? "");
        peer = net.Connect(opt.Host, opt.Port, writer);
        if (peer == null)
        {
            State.Fail("litenet_connect_null");
            StopNet(net);
            return 1;
        }

        var sw = Stopwatch.StartNew();
        bool loginSent = false;
        bool actionsDone = false;
        bool respawnTimedOut = false;
        ActionLoop.Stats? actionStats = null;

        try
        {
        while (sw.ElapsedMilliseconds < opt.TimeoutMs && !State.IsTerminal)
        {
            net.PollEvents();

            // Outbound
            lock (gate)
            {
                while (sendQueue.Count > 0 && peer != null)
                {
                    var pkt = sendQueue.Dequeue();
                    peer.Send(pkt, DeliveryMethod.ReliableOrdered);
                    State.PackagesSent++;
                }
            }

            // Inbound
            List<byte[]> batch;
            lock (gate)
            {
                batch = inbox.ToList();
                inbox.Clear();
            }

            foreach (var data in batch)
            {
                State.PackagesReceived++;
                // Challenge (raw 17 bytes, no channel game framing)
                if (PackageCodec.TryParseChallenge(data, out var ch))
                {
                    State.Advance(JoinStage.ChallengeReceived, ch.ToString());
                    Log($"STAGE ChallengeReceived: {ch}");
                    var reply = PackageCodec.BuildChallengeReply(data);
                    peer?.Send(reply, DeliveryMethod.ReliableOrdered);
                    State.PackagesSent++;
                    State.Advance(JoinStage.ChallengeReplied);
                    Log("STAGE ChallengeReplied");
                    continue;
                }

                // Diagnostics for live framing
                if (State.Stage <= JoinStage.PackageIdsReceived && State.PackagesReceived <= 6)
                {
                    int show = Math.Min(data.Length, 48);
                    Log($"RECV len={data.Length} hex={Convert.ToHexString(data.AsSpan(0, show))}");
                }

                // Live wire: [channel][size][comp][enc][count][inner packages] (comp may be deflate)
                var pkgs = PackageCodec.ParseChannelPayload(data);
                if (pkgs.Count == 0)
                {
                    string hint = data.Length >= 9
                        ? $" ch={data[0]} size={BitConverter.ToInt32(data, 1)} comp={data[5]} enc={data[6]} cnt={BitConverter.ToUInt16(data, 7)}"
                        : "";
                    Log($"RECV unparsed len={data.Length}{hint}");
                    continue;
                }

                foreach (var (id, body) in pkgs)
                {
                    if (verboseRecvLeft > 0 && !State.EverJoined)
                    {
                        verboseRecvLeft--;
                        Log($"RECV pkg id={id} bodyLen={body.Length}");
                    }
                    HandlePackage(id, body, ref loginSent, opt, Log, pkt =>
                    {
                        lock (gate)
                        {
                            while (sendQueue.Count >= MaxInbox)
                                sendQueue.Dequeue();
                            sendQueue.Enqueue(pkt);
                        }
                    });
                }
            }

            // After PackageIds, send PlayerLogin without waiting for another inbound package
            if (!loginSent
                && State.Stage == JoinStage.PackageIdsReceived
                && peer != null
                && !State.IsTerminal)
            {
                if (State.TryGetPackageId("NetPackagePlayerLogin", out ushort loginId))
                {
                    // VersionAuthorizer compares LongStringNoBuild (e.g. "V 3.1") to compVersion
                    string ver = PackageCodec.VersionLongString(State.ServerVersion);
                    var login = PackageCodec.BuildPlayerLogin(
                        loginId, opt.PlayerName + opt.ClientId, ver, ver);
                    peer.Send(login, DeliveryMethod.ReliableOrdered);
                    State.PackagesSent++;
                    loginSent = true;
                    State.Advance(JoinStage.LoginSent, $"pkgId={loginId} name={opt.PlayerName}{opt.ClientId}");
                    Log($"STAGE LoginSent: pkgId={loginId}");
                }
                else
                {
                    State.Fail("missing NetPackagePlayerLogin id in mappings");
                    Log("FAIL: no NetPackagePlayerLogin package id");
                }
            }

            // Life loop: walk until death → log → respawn → walk again, until timeout / max lives.
            if (State.Stage == JoinStage.Joined && !actionsDone && !opt.SkipActions)
            {
                var mode = opt.Mode;
                if (opt.WanderUntilDeath && mode == ActionLoop.BotMode.Wander)
                    mode = ActionLoop.BotMode.Wander;
                else if (!opt.WanderUntilDeath && mode == ActionLoop.BotMode.Wander)
                    mode = ActionLoop.BotMode.Mixed;
                State.BotModeName = mode.ToString();

                void PollInbox()
                {
                    net.PollEvents();
                    List<byte[]> inBatch;
                    lock (gate)
                    {
                        inBatch = inbox.ToList();
                        inbox.Clear();
                    }
                    foreach (var data in inBatch)
                    {
                        State.PackagesReceived++;
                        if (PackageCodec.TryParseChallenge(data, out _))
                            continue;
                        var pkgs = PackageCodec.ParseChannelPayload(data);
                        foreach (var (id, body) in pkgs)
                            HandlePackage(id, body, ref loginSent, opt, Log, pkt =>
                            {
                                lock (gate) sendQueue.Enqueue(pkt);
                            });
                    }
                    lock (gate)
                    {
                        while (sendQueue.Count > 0 && peer != null)
                        {
                            var pkt = sendQueue.Dequeue();
                            try { peer.Send(pkt, DeliveryMethod.ReliableOrdered); }
                            catch { break; }
                            State.PackagesSent++;
                        }
                    }
                }

                bool SendPkt(byte[] pkt)
                {
                    if (peer == null || State.IsTerminal) return false;
                    try
                    {
                        peer.Send(pkt, DeliveryMethod.ReliableOrdered);
                        return true;
                    }
                    catch { return false; }
                }

                int life = 0;
                while (sw.ElapsedMilliseconds < opt.TimeoutMs && !State.IsTerminal)
                {
                    life++;
                    long remainingMs = Math.Max(3_000, opt.TimeoutMs - sw.ElapsedMilliseconds);
                    Log(
                        $"LIFE start=#{life} entity={State.EntityId} deaths={State.DeathCount} " +
                        $"respawns={State.RespawnCount} remainingMs={remainingMs}");

                    // Each life gets a fresh death flag (respawn path clears it).
                    State.Died = false;
                    if (State.DeathCause is "timeout_alive")
                        State.DeathCause = "none";
                    opt.OnLifeStarted?.Invoke(State.EntityId);

                    actionStats = ActionLoop.Run(
                        State,
                        SendPkt,
                        new ActionLoop.Options
                        {
                            ActionCount = opt.ActionCount,
                            Seed = opt.ActionSeed + opt.ClientId + life * 997,
                            Mode = mode,
                            MaxDynamitePerLife = opt.MaxDynamitePerLife,
                            PingProbe = () => peer?.Ping ?? -1,
                            Death = opt.Death,
                            PaceMs = opt.PaceMs,
                            ChatPrefix = opt.PlayerName + opt.ClientId,
                            CohortSize = Math.Max(1, opt.CohortSize),
                            MaxLifetimeMs = (int)Math.Min(remainingMs, int.MaxValue),
                            Log = Log,
                            ShouldStop = () =>
                            {
                                if (State.IsTerminal || State.Died) return true;
                                // Telnet kill: server sets health=0 but often omits StatChanged to lite clients.
                                string myName = opt.PlayerName + opt.ClientId;
                                if (WorldDeathBus.TryConsumeKill(myName, out _))
                                {
                                    State.Died = true;
                                    State.DeathCause = "world_killed";
                                    Log($"DEATH cause=world_killed entity={State.EntityId} via=telnet_kill name={myName}");
                                    return true;
                                }
                                return false;
                            },
                            Poll = PollInbox,
                        });

                    if (State.IsTerminal)
                        break;

                    if (State.Died)
                    {
                        State.DeathCount++;
                        Log(
                            $"DEATH #{State.DeathCount} entity={State.EntityId} cause={State.DeathCause} " +
                            $"walks={State.WalkActions} jumps={State.JumpActions} " +
                            $"pos=({State.PosX:0.#},{State.PosY:0.#},{State.PosZ:0.#}) " +
                            $"mode={State.BotModeName} life={life}");

                        // Drain late GMSG for a moment
                        var drainUntil = sw.ElapsedMilliseconds + Math.Min(1500, opt.RespawnDelayMs);
                        while (sw.ElapsedMilliseconds < drainUntil && !State.IsTerminal)
                        {
                            PollInbox();
                            Thread.Sleep(15);
                        }

                        bool hitMaxLives = opt.MaxLives > 0 && State.DeathCount >= opt.MaxLives;
                        bool timeLeft = sw.ElapsedMilliseconds + 5_000 < opt.TimeoutMs;
                        if (!opt.Respawn || hitMaxLives || !timeLeft || State.IsTerminal)
                        {
                            Log(
                                $"NO_RESPAWN deaths={State.DeathCount} maxLives={opt.MaxLives} " +
                                $"respawn={opt.Respawn} timeLeft={timeLeft}");
                            break;
                        }

                        // Respawn delay then request spawn
                        var waitUntil = sw.ElapsedMilliseconds + Math.Max(200, opt.RespawnDelayMs);
                        while (sw.ElapsedMilliseconds < waitUntil && !State.IsTerminal)
                        {
                            PollInbox();
                            Thread.Sleep(20);
                        }
                        if (State.IsTerminal)
                            break;

                        if (!TryRequestRespawn(SendPkt, Log))
                        {
                            Log("RESPAWN fail: could not send RequestToSpawnPlayer");
                            break;
                        }

                        State.AwaitingRespawn = true;
                        bool gotSpawn = false;
                        var respawnDeadline = sw.ElapsedMilliseconds + Math.Max(1_000, opt.RespawnTimeoutMs);
                        while (sw.ElapsedMilliseconds < respawnDeadline && !State.IsTerminal)
                        {
                            PollInbox();
                            if (!State.AwaitingRespawn && !State.Died)
                            {
                                gotSpawn = true;
                                break;
                            }
                            Thread.Sleep(15);
                        }
                        if (!gotSpawn)
                        {
                            // Server never confirmed the respawn in time: this is a
                            // dropout under load, not a successful session. Mark it so
                            // the final gate does not score it as PASS.
                            respawnTimedOut = true;
                            State.DeathCause = "respawn_timeout";
                            Log(
                                $"RESPAWN timeout awaiting spawn entity={State.EntityId} " +
                                $"awaiting={State.AwaitingRespawn}");
                            break;
                        }
                        Log(
                            $"RESPAWN ok #{State.RespawnCount} entity={State.EntityId} " +
                            $"pos=({State.PosX:0.#},{State.PosY:0.#},{State.PosZ:0.#}) → walk again");
                        continue; // next life
                    }

                    // No death this life: overall lifetime expired or action count done
                    if (State.DeathCause is "timeout_alive" || opt.ActionCount > 0)
                    {
                        Log(
                            $"ALIVE_END entity={State.EntityId} cause={State.DeathCause} " +
                            $"walks={State.WalkActions} life={life}");
                    }
                    break;
                }

                actionsDone = true;
                Log(
                    $"DISCONNECT entity={State.EntityId} deaths={State.DeathCount} " +
                    $"respawns={State.RespawnCount} walks={State.WalkActions} " +
                    $"lastCause={State.DeathCause}");
                break;
            }

            if (opt.SkipActions && State.Stage == JoinStage.Joined)
                break;

            Thread.Sleep(10);
        }

        // Graceful disconnect so the server frees the player slot immediately
        // instead of holding a ghost until its own DisconnectTimeout. Ghost
        // accumulation from hard-killed cohorts causes NetPackagePlayerDenied
        // (reason=2, world-full) on later joins and corrupts spawn state.
        try { net.DisconnectAll(); net.PollEvents(); Thread.Sleep(120); } catch { }
        }
        finally
        {
            // Guarantee the socket + manager are released on every path,
            // including an exception inside the poll/action loop.
            StopNet(net);
        }

        // Success: reached joined and ran actions (or skip-actions). World death or
        // timeout_alive after a long walk both count as pass.
        if (State.EverJoined && !respawnTimedOut && (actionsDone || opt.SkipActions
            || State.Stage == JoinStage.Joined || State.Stage == JoinStage.Disconnected))
        {
            Log(
                $"PASS joined entity={State.EntityId} walks={State.WalkActions} jumps={State.JumpActions} " +
                $"deaths={State.DeathCount} respawns={State.RespawnCount} " +
                $"lastDied={State.Died} lastCause={State.DeathCause} stage={State.Stage}");
            return 0;
        }

        Log($"FAIL lastStage={State.Stage} reason={State.FailReason ?? "timeout"} " +
            $"recv={State.PackagesReceived} sent={State.PackagesSent} everJoined={State.EverJoined}");
        return 1;
    }

    /// <summary>127.0.0.0/8 unique bind for client index (avoids dedicated per-IP connect throttle).</summary>
    public static string LoopbackBindForIndex(int index)
    {
        // 127.a.b.c with a=0..255, skip 127.0.0.0
        int n = Math.Abs(index) % (256 * 256 * 254) + 1;
        int c = n % 256;
        int b = (n / 256) % 256;
        int a = (n / (256 * 256)) % 256;
        if (a == 0 && b == 0 && c == 0) c = 1;
        return $"127.{a}.{b}.{c}";
    }

    void HandlePackage(
        ushort id,
        byte[] body,
        ref bool loginSent,
        Options opt,
        Action<string> log,
        Action<byte[]> enqueue)
    {
        // PackageIds is always id 0 until remapped; also match by content heuristic
        bool looksLikePackageIds = body.Length > 16 && !State.PackageIds.ContainsKey("NetPackagePlayerLogin");
        string? typeName = null;
        foreach (var kv in State.PackageIds)
            if (kv.Value == id) { typeName = kv.Key; break; }
        // After join, high-volume entity/chunk packages would flood logs for hour-long runs.
        bool noisy = typeName is "NetPackageEntityPosAndRot" or "NetPackageEntityRelPosAndRot"
            or "NetPackageEntityAliveFlags" or "NetPackageEntityStat" or "NetPackageEntityStats"
            or "NetPackagePlayerStats" or "NetPackageEntityMotion" or "NetPackageChunk"
            or "NetPackageChunkClusterInfo" or "NetPackageWaterUpdate" or "NetPackageTileEntity"
            or "NetPackageEntitySpawn" or "NetPackageEntityRemove" or "NetPackageEntityDespawn"
            or "NetPackageEntityAnimationData" or "NetPackageEntityLookAt";
        if (!State.EverJoined)
        {
            if (typeName == null && State.PackageIds.Count > 0)
                log($"RECV unmapped pkg id={id} bodyLen={body.Length}");
            else if (typeName != null && !noisy)
                log($"RECV type={typeName} id={id} bodyLen={body.Length}");
        }
        else if (typeName is "NetPackageSimpleChat" or "NetPackageChat" or "NetPackageGameMessage"
                 or "NetPackagePlayerDenied" or "NetPackagePlayerSpawnedInWorld" or "NetPackagePlayerId")
        {
            log($"RECV type={typeName} id={id} bodyLen={body.Length}");
        }

        // One-time handshake step: guard BOTH recognition paths by stage so a
        // second PackageIds packet cannot re-run ApplyPackageMappings (which
        // clears the id table) and scramble routing mid-session.
        if ((typeName == "NetPackagePackageIds" || (id == 0 && looksLikePackageIds))
            && State.Stage < JoinStage.PackageIdsReceived)
        {
            try
            {
                var (ver, maps, eac) = PackageCodec.ParsePackageIdsBody(body);
                State.ServerVersion = ver;
                State.ApplyPackageMappings(maps);
                log($"STAGE PackageIdsReceived: ver={PackageCodec.VersionLongString(ver)} " +
                    $"({ver.ReleaseType}.{ver.Major}.{ver.Minor}.{ver.Build}) maps={maps.Length} eac={eac}");
                if (eac)
                    log("NOTE: serverUseEAC=true — login may be denied without EAC");
            }
            catch (Exception ex)
            {
                State.Fail($"package_ids_parse: {ex.Message}");
                log($"FAIL package_ids: {ex.Message}");
            }
            return;
        }

        // Server waits for client to echo AuthConfirmation before AuthFinalizer proceeds.
        if (typeName == "NetPackageAuthConfirmation")
        {
            if (State.TryGetPackageId("NetPackageAuthConfirmation", out ushort confId))
            {
                enqueue(PackageCodec.BuildAuthConfirmation(confId));
                State.PackagesSent++;
                log("STAGE AuthConfirmation echoed");
            }
            return;
        }

        if (typeName == "NetPackageAuthState")
        {
            try
            {
                using var ms = new MemoryStream(body);
                using var r = new BinaryReader(ms, System.Text.Encoding.UTF8);
                string key = r.ReadString();
                log($"STAGE AuthState: {key}");
            }
            catch { /* ignore */ }
            return;
        }

        if (typeName == "NetPackagePlayerLoginAnswer")
        {
            try
            {
                var (allowed, data) = PackageCodec.ParseLoginAnswerBody(body);
                State.Advance(JoinStage.LoginAnswered, $"allowed={allowed} data={data}");
                log($"STAGE LoginAnswered: allowed={allowed} dataLen={data?.Length ?? 0}");
                if (!allowed)
                {
                    State.Fail($"login_denied: {data}");
                    return;
                }
                // Real client continues with RequestToEnterGame after PlayerAllowed
                if (State.TryGetPackageId("NetPackageRequestToEnterGame", out ushort enterId))
                {
                    enqueue(PackageCodec.BuildRequestToEnterGame(enterId));
                    State.PackagesSent++;
                    log($"STAGE RequestToEnterGame sent pkgId={enterId}");
                }
                else
                    log("WARN: no NetPackageRequestToEnterGame mapping");
            }
            catch (Exception ex)
            {
                State.Fail($"login_answer_parse: {ex.Message}");
            }
            return;
        }

        // After world info / spawn points, request spawn once
        if (typeName is "NetPackageWorldInfo" or "NetPackageWorldSpawnPoints" or "NetPackageGameStats")
        {
            log($"STAGE {typeName} received bodyLen={body.Length}");
            if (!State.SpawnRequested
                && State.TryGetPackageId("NetPackageRequestToSpawnPlayer", out ushort spawnReqId))
            {
                State.SpawnRequested = true;
                // Vary view distance per bot (4..12 chunks) so the cohort's chunk
                // residency spreads realistically instead of every client
                // demanding an identical bubble.
                enqueue(PackageCodec.BuildRequestToSpawnPlayer(
                    spawnReqId, chunkViewDim: 4 + (opt.ClientId % 9)));
                State.PackagesSent++;
                log($"STAGE RequestToSpawnPlayer sent pkgId={spawnReqId}");
            }
            return;
        }

        if (typeName == "NetPackagePlayerSpawnedInWorld")
        {
            try
            {
                var (entityId, x, y, z) = PackageCodec.ParseSpawnedBody(body);
                ApplySpawn(entityId, x, y, z, log, via: "PlayerSpawnedInWorld");
            }
            catch (Exception ex)
            {
                State.Fail($"spawn_parse: {ex.Message}");
            }
            return;
        }

        if (typeName == "NetPackagePlayerId")
        {
            // After RequestToSpawnPlayer the server creates the entity and sends PlayerId
            // (not NetPackagePlayerSpawnedInWorld). Body starts with entityId:i32.
            if (body.Length >= 4)
            {
                int entityId = BitConverter.ToInt32(body, 0);
                State.Advance(JoinStage.PlayerIdReceived, $"entityId={entityId} bodyLen={body.Length}");
                log($"STAGE PlayerIdReceived: entityId={entityId} bodyLen={body.Length}");
                if (entityId > 0 && (State.AwaitingRespawn || (State.SpawnRequested && !State.IsJoined)))
                {
                    float x = State.PosX, y = State.PosY, z = State.PosZ;
                    if (!State.AwaitingRespawn && x == 0 && z == 0)
                    {
                        x = 256;
                        y = 72;
                        z = 256;
                    }
                    ApplySpawn(entityId, x, y, z, log, via: "PlayerId");
                }
                else if (entityId <= 0)
                    // A non-positive entity id can never advance to Joined; fail
                    // now instead of looping until the (up to 1h) timeout.
                    State.Fail("player_id_invalid_entity");
                else if (!State.IsJoined)
                    State.EntityId = entityId;
                // Already joined: ignore a duplicate PlayerId. Overwriting our
                // established EntityId with a stray/second value would break all
                // entity-keyed packet routing for the rest of the session.
            }
            return;
        }

        if (typeName == "NetPackagePlayerDenied")
        {
            try
            {
                var (reason, custom) = PackageCodec.ParsePlayerDeniedBody(body);
                State.Fail($"player_denied reason={reason} custom={custom}");
                log($"STAGE Failed: player_denied reason={reason} custom={custom}");
            }
            catch (Exception ex)
            {
                State.Fail($"player_denied ({ex.Message})");
                log($"STAGE Failed: player_denied parse_err={ex.Message}");
            }
            return;
        }

        // Adopt the server's authoritative Y for our own player. Bots are
        // position-authoritative once joined, but they enter the world with a
        // guessed height (join reply omits ground Y); if they never correct it
        // they float over real terrain, which breaks server-side spawn-point
        // search near the player and makes spawned zombies unable to reach us.
        if (State.EverJoined && State.EntityId > 0
            && typeName is "NetPackageEntityPosAndRot" or "NetPackageEntityTeleport")
        {
            try
            {
                var (eid, px, py, pz, _) = PackageCodec.ParsePosAndRotBody(body);
                // Reject non-finite coordinates: a malformed Infinity/NaN from the
                // server would otherwise poison PosX/Y/Z and propagate into every
                // subsequent move the bot sends. (NaN fails py>1f, but Inf passes,
                // and px/pz are never range-checked.)
                if (eid == State.EntityId && py > 1f
                    && float.IsFinite(px) && float.IsFinite(py) && float.IsFinite(pz))
                {
                    // Continuously reconcile with the server's authoritative
                    // position for our own entity, like a real client. A one-shot
                    // adopt leaves the bot reporting a stale ground Y as it walks
                    // onto higher terrain, so it ends up inside blocks and the
                    // server's move validation snaps it back near spawn (bots then
                    // cannot roam). Adopting every correction lets Y track terrain.
                    bool bigCorrection =
                        Math.Abs(py - State.PosY) > 2f
                        || Math.Abs(px - State.PosX) > 2f
                        || Math.Abs(pz - State.PosZ) > 2f;
                    if (bigCorrection && (State.CorrectionLogged++ < 20))
                        log($"SERVER-CORRECT eid={eid} ({State.PosX:0.#},{State.PosY:0.#},"
                            + $"{State.PosZ:0.#}) -> ({px:0.#},{py:0.#},{pz:0.#})");
                    State.PosY = py;
                    if (bigCorrection)
                    {
                        // Hard correction (server rejected our X/Z): accept it so
                        // we do not keep fighting the validator from a bad spot.
                        State.PosX = px;
                        State.PosZ = pz;
                    }
                    State.GroundAdopted = true;
                }
            }
            catch { /* malformed/short body: ignore */ }
        }

        // After join: best-effort world-death signals (stat health, chat GMSG, entity remove).
        if (State.EverJoined && !State.Died)
            TryDetectWorldDeath(typeName, body, opt, log);

        _ = loginSent;
        _ = enqueue;
    }

    void ApplySpawn(int entityId, float x, float y, float z, Action<string> log, string via)
    {
        bool wasRespawn = State.AwaitingRespawn;
        State.EntityId = entityId;
        State.PosX = x;
        // Server sometimes replies with y<=1 ("pick your own ground"). A wrong
        // hardcoded height leaves the bot floating/embedded, which breaks
        // server-side spawn-point search near the player. Last known y is the
        // best available guess.
        State.PosY = y > 1f ? y : (State.PosY > 1f ? State.PosY : 72f);
        State.PosZ = z;
        State.Advance(JoinStage.SpawnedInWorld, $"entity={entityId} pos=({x},{y},{z}) via={via}");
        log($"STAGE SpawnedInWorld: entity={entityId} pos=({x},{y},{z}) via={via}");
        if (wasRespawn)
        {
            State.RespawnCount++;
            State.ClearDeathForNewLife();
            log($"STAGE Respawned: entity={entityId} count={State.RespawnCount}");
        }
        else
        {
            State.MarkJoined();
            log($"STAGE Joined: entity={entityId}");
        }
        // Ensure stage stays Joined after respawn (Advance may not regress, MarkJoined already Joined)
        if (State.EverJoined && State.Stage == JoinStage.SpawnedInWorld)
            State.MarkJoined();
    }

    bool TryRequestRespawn(Func<byte[], bool> send, Action<string> log)
    {
        if (!State.TryGetPackageId("NetPackageRequestToSpawnPlayer", out ushort spawnReqId))
        {
            log("RESPAWN missing NetPackageRequestToSpawnPlayer id");
            return false;
        }
        State.SpawnRequested = true;
        State.AwaitingRespawn = true;
        // Keep Died=true until spawn arrives so action loops do not restart early.
        var pkt = PackageCodec.BuildRequestToSpawnPlayer(
            spawnReqId, chunkViewDim: 4 + (Math.Abs(State.EntityId) % 9));
        if (!send(pkt))
        {
            State.AwaitingRespawn = false;
            return false;
        }
        State.PackagesSent++;
        log($"STAGE RequestToSpawnPlayer (respawn) pkgId={spawnReqId} afterDeaths={State.DeathCount}");
        return true;
    }

    /// <summary>
    /// Mark world death when health hits 0, chat mentions die/kill, or entity removed.
    /// NetPackageEntityStatChanged body (from Assembly-CSharp write IL):
    /// entityId:i32, instigatorId:i32, enumStat:u8 (0=Health), value:f32, max:f32, maxMod:f32.
    /// </summary>
    void TryDetectWorldDeath(string? typeName, byte[] body, Options opt, Action<string> log)
    {
        if (typeName == "NetPackageEntityStatChanged" && body.Length >= 21 && State.EntityId > 0)
        {
            int eid = BitConverter.ToInt32(body, 0);
            byte estat = body[8];
            float value = BitConverter.ToSingle(body, 9);
            // EnumStat.Health = 0
            if (eid == State.EntityId && estat == 0 && value <= 0.01f)
            {
                State.Died = true;
                State.DeathCause = "world_killed";
                log($"DEATH cause=world_killed entity={eid} via=EntityStatChanged health={value:0.##}");
            }
            return;
        }

        if (typeName is "NetPackageEntityRemove" or "NetPackageEntityDespawn"
            or "NetPackageRemoveEntity" or "NetPackageEntityDestroy")
        {
            if (body.Length >= 4)
            {
                int eid = BitConverter.ToInt32(body, 0);
                if (eid == State.EntityId && State.EntityId > 0)
                {
                    State.Died = true;
                    State.DeathCause = "world_death";
                    log($"DEATH cause=world_death entity={eid} via=entity_remove");
                }
            }
            return;
        }

        if (typeName is not ("NetPackageSimpleChat" or "NetPackageChat"
            or "NetPackageGameMessage" or "NetPackageChatMessage"))
            return;

        string text = ExtractPrintable(body);
        if (string.IsNullOrEmpty(text) || text.Length < 4) return;

        log($"CHAT {(text.Length > 160 ? text[..160] : text)}");

        string lower = text.ToLowerInvariant();
        string ourName = (opt.PlayerName + opt.ClientId).ToLowerInvariant();
        // Match ONLY our own unique name, word-bounded. The old code also matched
        // the bare "refake" prefix (every bot is REFake<N>, so one bot's death GMSG
        // flipped Died on the whole cohort) and fell back to any GMSG containing
        // "player" (killing a bystander bot). Both corrupted death/respawn metrics.
        bool aboutUs = ContainsWord(lower, ourName);
        bool deathWords = lower.Contains("died") || lower.Contains("killed")
            || lower.Contains("drowned") || lower.Contains("drown")
            || lower.Contains("zombie") || lower.Contains("radiation")
            || lower.Contains("suffocat") || lower.Contains("bled out")
            || lower.Contains("was slain");
        if (!deathWords) return;
        if (!aboutUs) return;

        State.Died = true;
        if (lower.Contains("drown"))
            State.DeathCause = "world_drown";
        else if (lower.Contains("zombie") || lower.Contains("killed by"))
            State.DeathCause = "world_killed";
        else if (lower.Contains("radiation"))
            State.DeathCause = "world_radiation";
        else
            State.DeathCause = "world_death";
        string snippet = text.Length > 120 ? text[..120] : text;
        log($"DEATH cause={State.DeathCause} entity={State.EntityId} via=chat text={snippet}");
    }

    // Whole-word substring match (allocation-free): "refake3" matches "refake3 died"
    // but not "refake33", so one bot's death GMSG never flips a differently-numbered bot.
    static bool ContainsWord(string haystack, string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        int i = 0;
        while ((i = haystack.IndexOf(word, i, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
            int end = i + word.Length;
            bool rightOk = end == haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (leftOk && rightOk) return true;
            i = end;
        }
        return false;
    }

    static string ExtractPrintable(byte[] body)
    {
        if (body.Length == 0) return "";
        try
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, System.Text.Encoding.UTF8);
            if (body.Length >= 2)
            {
                string s = r.ReadString();
                if (s.Length >= 3 && s.Any(char.IsLetter))
                    return s;
            }
        }
        catch { /* fall through */ }

        var sb = new System.Text.StringBuilder();
        foreach (byte b in body)
        {
            if (b is >= 32 and < 127) sb.Append((char)b);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>In-process join against <see cref="MockGameServer"/> (shipped path for CI).</summary>
    public static int RunSelfTestJoin(int actionCount, int seed, Action<string>? log, out JoinStateMachine sm)
    {
        using var server = new MockGameServer();
        server.Start(0);
        using var cts = new CancellationTokenSource();
        var poll = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                server.Poll();
                Thread.Sleep(2);
            }
        });

        var client = new GameJoinClient();
        // CI: die (client drown for mock), respawn, walk again. Live default is still no self-kill.
        int rc = client.Run(new Options
        {
            Host = "127.0.0.1",
            Port = server.Port,
            PlayerName = "REFake",
            TimeoutMs = 25_000,
            ActionCount = Math.Max(actionCount, 12),
            ActionSeed = seed,
            ClientId = 1,
            Mode = ActionLoop.BotMode.Wander,
            Death = ActionLoop.DeathMethod.Drown,
            WanderUntilDeath = true,
            Respawn = true,
            MaxLives = 2,
            RespawnDelayMs = 100,
            CohortSize = 1,
            PaceMs = 5, // fast for CI
            Log = log,
        });
        sm = client.State;

        // drain
        var until = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < until) { server.Poll(); Thread.Sleep(5); }
        cts.Cancel();
        try { poll.Wait(1000); } catch { /* ignore */ }

        log?.Invoke(
            $"SELFTEST server walksRecv={server.WalkPackages} jumpsRecv={server.JumpPackages} " +
            $"flagsRecv={server.FlagPackages} chatRecv={server.ChatPackages} lookRecv={server.LookPackages} " +
            $"drownsRecv={server.DrownPackages} suicidesRecv={server.SuicidePackages} killsRecv={server.KillPackages} " +
            $"logins={server.LoginsAccepted} challengesOk={server.ChallengesOk} " +
            $"deaths={sm.DeathCount} respawns={sm.RespawnCount}");

        if (rc != 0 || !sm.EverJoined)
            return 1;
        if (sm.WalkActions < 1)
        {
            log?.Invoke($"FAIL actions walks={sm.WalkActions}");
            return 1;
        }
        if (server.WalkPackages < 1)
        {
            log?.Invoke("FAIL server did not observe walk packages");
            return 1;
        }
        if (sm.DeathCount < 2)
        {
            log?.Invoke($"FAIL expected 2 deaths for respawn loop, got {sm.DeathCount}");
            return 1;
        }
        if (sm.RespawnCount < 1)
        {
            log?.Invoke($"FAIL expected respawn after first death, got {sm.RespawnCount}");
            return 1;
        }
        return 0;
    }
}
