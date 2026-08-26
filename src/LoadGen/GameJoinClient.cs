using System.Buffers.Binary;
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

    /// <summary>0 = no shutdown pass yet; 1 = a pass is running or done.</summary>
    static int _shutdownPassStarted;

    /// <summary>0 = normal operation; 1 = DisconnectAllActive is sweeping. Live
    /// client loops observe this and unwind BEFORE the sweep touches any
    /// manager: a NetManager is single-threaded by contract here (one poll loop
    /// per client), so DisconnectAll/Stop from the signal-handler thread must
    /// never overlap that client's own PollEvents/Send on the same manager.</summary>
    static int _shutdownRequested;

    /// <summary>True once a shutdown sweep started; client loops check it and exit.</summary>
    public static bool ShutdownRequested => System.Threading.Volatile.Read(ref _shutdownRequested) != 0;

    /// <summary>Test seam: clear the one-shot shutdown state so more than one
    /// unit test can exercise <see cref="DisconnectAllActive"/> per process.</summary>
    internal static void ResetShutdownForTests()
    {
        System.Threading.Volatile.Write(ref _shutdownRequested, 0);
        _shutdownPassStarted = 0;
    }

    /// <summary>Serializes manager teardown: the shutdown sweep and a client's
    /// own StopNet both drive NetManager.Stop, and concurrent calls corrupt the
    /// non-thread-safe internals even when each caller holds a valid reference.</summary>
    static readonly object SweepGate = new();

    public static void DisconnectAllActive()
    {
        // Normal exits reach ProcessExit after every Run() already called
        // StopNet (which removes its manager), so there is nothing to sweep;
        // skip the grace sleeps instead of taxing every invocation, including
        // --version/--help and clean single-bot runs, ~500 ms of pure delay.
        // A sweep that raced a just-started client is impossible here: bot
        // tasks are joined before Main returns, and Ctrl+C goes through the
        // CancelKeyPress handler with a live registry.
        if (ActiveNets.IsEmpty)
            return;
        // Both Console.CancelKeyPress and ProcessExit land here, and a double
        // SIGINT can invoke the handler again while the first pass sleeps. Two
        // passes would drive DisconnectAll/Stop into the same non-thread-safe
        // NetManagers concurrently, so run the sweep exactly once per process.
        if (System.Threading.Interlocked.CompareExchange(ref _shutdownPassStarted, 1, 0) != 0)
            return;
        // Ask every live client loop to unwind first. They observe the flag at
        // each poll/pace slice (~20ms granularity), exit Run, and release their
        // manager through StopNet under SweepGate; the grace period covers that
        // unwind before the sweep starts mutating anything itself.
        System.Threading.Interlocked.Exchange(ref _shutdownRequested, 1);
        System.Threading.Thread.Sleep(300);
        // No PollEvents() here: with UnsyncedEvents=false it would dequeue and
        // dispatch pending LiteNetLib events on THIS thread, running each bot's
        // listener handlers (peer assignment, State/log mutations) concurrently
        // with that bot's own poll loop. DisconnectAll sends the BYE packet
        // synchronously; the sleep just gives it time to drain.
        lock (SweepGate)
        {
            // A manager whose socket already died throws from DisconnectAll/Stop.
            // This is the last teardown pass on the way out of the process, so
            // one dead manager must not stop the sweep reaching the rest; there
            // is no later stage that could observe the fault anyway.
            foreach (var n in ActiveNets.Keys)
            {
                try { n.DisconnectAll(); }
                catch (Exception ex) { Console.Error.WriteLine($"shutdown disconnect: {ex.Message}"); }
            }
            System.Threading.Thread.Sleep(200);
            foreach (var n in ActiveNets.Keys)
            {
                try { n.Stop(); }
                catch (Exception ex) { Console.Error.WriteLine($"shutdown stop: {ex.Message}"); }
            }
        }
    }

    /// <summary>Stop a manager and drop it from the live set (idempotent). Every
    /// Run() exit path must call this so no UDP socket or manager leaks.</summary>
    static void StopNet(LiteNetLib.NetManager net)
    {
        ActiveNets.TryRemove(net, out _);
        // Releasing a socket must never mask the run's own result: this runs on
        // every Run() exit path, including the one carrying a gate failure.
        lock (SweepGate)
        {
            try { net.Stop(); }
            catch (Exception ex) { Console.Error.WriteLine($"net stop: {ex.Message}"); }
        }
    }

    public JoinStateMachine State { get; } = new();

    BenchClock? _bench;
    bool _observerSinkFaultLogged;

    public sealed class Options
    {
        public string Host { get; set; } = "127.0.0.1";
        // Bots speak LiteNetLib, so they must hit the LiteNet data port =
        // ServerPort + 2 (26902 for the stock 26900 server). ServerPort itself
        // (26900) is the game client's "Connect to IP" port; a bot there fails
        // with ConnectionFailed.
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
        /// <summary>Bot behaviour mode (see <see cref="ActionLoop.BotMode"/>).</summary>
        public ActionLoop.BotMode Mode { get; set; } = ActionLoop.BotMode.Wander;
        /// <summary>Client self-kill method. Default None = wait for zombies/rad/water/server.</summary>
        public ActionLoop.DeathMethod Death { get; set; } = ActionLoop.DeathMethod.None;
        /// <summary>Optional pace override in ms (-1 = mode default).</summary>
        public int PaceMs { get; set; } = -1;

        /// <summary>Dynamite cap per life (Demolition mode raises this).</summary>
        public int MaxDynamitePerLife { get; set; } = ActionLoop.DefaultMaxDynamitePerLife;
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
        /// <summary>Optional filtered replicated-state observer. Null keeps ordinary load runs quiet.</summary>
        public NetworkStateObserver? StateObserver { get; set; }
        /// <summary>Optional cohort bench clock; counts deaths/respawns inside the window.</summary>
        public BenchClock? Bench { get; set; }
    }

    public int Run(Options opt)
    {
        void Log(string msg)
        {
            string line = $"[{DateTime.UtcNow:O}] [join#{opt.ClientId}] {msg}";
            opt.Log?.Invoke(line);
            State.Note(line);
        }

        _bench = opt.Bench;

        string bindIp = string.IsNullOrWhiteSpace(opt.LocalBindIp) ? "0.0.0.0" : opt.LocalBindIp!;
        // Preflight only: prove the loopback bind and hostname resolve before
        // LiteNetLib starts, then release the socket. Holding it for the whole
        // Run() cost one idle fd per bot for the session (a 1000-bot soak held
        // 1000 sockets LiteNetLib never used; the real traffic rides the
        // NetManager's own socket).
        try
        {
            using (var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(bindIp == "0.0.0.0" ? "127.0.0.1" : bindIp), 0)))
            {
                udp.Connect(opt.Host, opt.Port);
            }
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
        const int MaxQueued = 2_000;
        int verboseRecvLeft = 40; // throttle wire logs after join

        // Shared queue plumbing for the join-phase main loop and the post-join
        // PollInbox: one enqueue cap, one drain, one flush, so the two phases
        // cannot drift into different bounds or send semantics.
        void EnqueueSend(byte[] pkt)
        {
            lock (gate)
            {
                // Cap mirrors the inbox: a stalled peer must not retain every
                // frame the action loop produces for the rest of the session.
                while (sendQueue.Count >= MaxQueued)
                    sendQueue.Dequeue();
                sendQueue.Enqueue(pkt);
            }
        }

        void FlushSends()
        {
            lock (gate)
            {
                while (sendQueue.Count > 0 && peer != null)
                {
                    var pkt = sendQueue.Dequeue();
                    // A send fault (peer died mid-session) must end the flush,
                    // not escape past the PASS/FAIL summary. The next
                    // PollEvents dispatches PeerDisconnectedEvent, which
                    // terminates the join through the normal terminal path.
                    try { peer.Send(pkt, DeliveryMethod.ReliableOrdered); }
                    catch { break; }
                    State.PackagesSent++;
                }
            }
        }

        void DrainInbox(List<byte[]> batch)
        {
            lock (gate)
            {
                foreach (var item in inbox)
                    batch.Add(item);
                inbox.Clear();
            }
        }

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
                while (inbox.Count >= MaxQueued)
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

        // Always put password string (even empty): server GetString() vs serverPassword.
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
        // Reused drain buffers: allocating a List per poll iteration was steady
        // GC churn at cohort scale (every bot drains ~50-100x/sec).
        var recvBatch = new List<byte[]>();
        try
        {
            while (sw.ElapsedMilliseconds < opt.TimeoutMs && !State.IsTerminal && !ShutdownRequested)
            {
                net.PollEvents();

                // Outbound, then inbound
                FlushSends();
                DrainInbox(recvBatch);

                foreach (var data in recvBatch)
                {
                    State.PackagesReceived++;
                    // Challenge (raw 17 bytes, no channel game framing)
                    if (PackageCodec.TryParseChallenge(data, out var ch))
                    {
                        State.Advance(JoinStage.ChallengeReceived, ch.ToString());
                        Log($"STAGE ChallengeReceived: {ch}");
                        var reply = PackageCodec.BuildChallengeReply(data);
                        // Peer died between challenge and echo: the disconnect
                        // event terminates the join; the fault must not escape.
                        // Like the login send below, leave a breadcrumb so a
                        // stalled join shows where the last send failed.
                        try
                        {
                            peer?.Send(reply, DeliveryMethod.ReliableOrdered);
                            State.PackagesSent++;
                        }
                        catch (Exception ex)
                        {
                            Log($"FAIL challenge_send: {ex.Message}");
                        }
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
                            ? $" ch={data[0]} size={BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1))} comp={data[5]} enc={data[6]} cnt={BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(7))}"
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
                        HandlePackage(id, body, opt, Log, EnqueueSend);
                    }
                }
                recvBatch.Clear();

                // After PackageIds, send PlayerLogin without waiting for another inbound package
                if (!loginSent
                    && State.Stage == JoinStage.PackageIdsReceived
                    && peer != null
                    && !State.IsTerminal)
                {
                    if (State.TryGetPackageId("NetPackagePlayerLogin", out ushort loginId))
                    {
                        // EMPIRICAL 2026-08-22: stock V3.1.0 (b14) accepts the display
                        // form "V 3.1.0" and KICKS "V 3.10" (VersionMismatch=4); the
                        // LongStringNoBuild theory (b5c3069) is wrong for stock.
                        string ver = PackageCodec.VersionLongString(State.ServerVersion);
                        var login = PackageCodec.BuildPlayerLogin(
                            loginId, opt.PlayerName + opt.ClientId, ver, ver);
                        try
                        {
                            peer.Send(login, DeliveryMethod.ReliableOrdered);
                            State.PackagesSent++;
                        }
                        catch (Exception ex)
                        {
                            // Breadcrumb, not an escape: the disconnect event the
                            // next poll dispatches fails this join with its real
                            // reason instead of an EX line from a raw send fault.
                            Log($"FAIL login_send: {ex.Message}");
                        }
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
                    if (!opt.WanderUntilDeath && mode == ActionLoop.BotMode.Wander)
                        mode = ActionLoop.BotMode.Mixed;
                    State.BotModeName = mode.ToString();

                    var pollBatch = new List<byte[]>();
                    void PollInbox()
                    {
                        net.PollEvents();
                        DrainInbox(pollBatch);
                        foreach (var data in pollBatch)
                        {
                            State.PackagesReceived++;
                            if (PackageCodec.TryParseChallenge(data, out _))
                                continue;
                            var pkgs = PackageCodec.ParseChannelPayload(data);
                            foreach (var (id, body) in pkgs)
                                HandlePackage(id, body, opt, Log, EnqueueSend);
                        }
                        pollBatch.Clear();
                        FlushSends();
                    }

                    bool SendPkt(byte[] pkt)
                    {
                        if (peer == null || State.IsTerminal || ShutdownRequested) return false;
                        try
                        {
                            peer.Send(pkt, DeliveryMethod.ReliableOrdered);
                            return true;
                        }
                        catch { return false; }
                    }

                    int life = 0;
                    while (sw.ElapsedMilliseconds < opt.TimeoutMs && !State.IsTerminal && !ShutdownRequested)
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

                        // Computed once per life: ShouldStop runs on every pace
                        // slice (~50x/s per bot), so the name must not be
                        // concatenated inside that closure.
                        string botName = opt.PlayerName + opt.ClientId;
                        ActionLoop.Run(
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
                                ChatPrefix = botName,
                                CohortSize = Math.Max(1, opt.CohortSize),
                                MaxLifetimeMs = (int)Math.Min(remainingMs, int.MaxValue),
                                Log = Log,
                                Bench = opt.Bench,
                                ShouldStop = () =>
                                {
                                    if (State.IsTerminal || State.Died || ShutdownRequested) return true;
                                    // Telnet kill: server sets health=0 but often omits StatChanged to lite clients.
                                    if (WorldDeathBus.TryConsumeKill(botName, out _))
                                    {
                                        State.Died = true;
                                        State.DeathCause = "world_killed";
                                        Log($"DEATH cause=world_killed entity={State.EntityId} via=telnet_kill name={botName}");
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
                            opt.Bench?.OnDeath();
                            Log(
                                $"DEATH #{State.DeathCount} entity={State.EntityId} cause={State.DeathCause} " +
                                $"walks={State.WalkActions} jumps={State.JumpActions} " +
                                $"pos=({State.PosX:0.#},{State.PosY:0.#},{State.PosZ:0.#}) " +
                                $"mode={State.BotModeName} life={life}");

                            // Drain late GMSG for a moment
                            var drainUntil = sw.ElapsedMilliseconds + Math.Min(1500, opt.RespawnDelayMs);
                            while (sw.ElapsedMilliseconds < drainUntil && !State.IsTerminal && !ShutdownRequested)
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
                            while (sw.ElapsedMilliseconds < waitUntil && !State.IsTerminal && !ShutdownRequested)
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
                            while (sw.ElapsedMilliseconds < respawnDeadline && !State.IsTerminal && !ShutdownRequested)
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
            // Skipped once a shutdown sweep started: the sweep owns every live
            // manager at that point, and two threads mutating one NetManager is
            // exactly the race this file forbids.
            // A dead socket throws here; the finally below still releases the
            // manager, and this courtesy BYE has no result to report.
            if (!ShutdownRequested)
                try { net.DisconnectAll(); net.PollEvents(); Thread.Sleep(120); }
                catch (Exception) { }
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

    /// <summary>Index-space stride between one client's rejoin attempts. The
    /// identity is two-dimensional (client, attempt) but addresses are one
    /// dimensional, so the linear map collides exactly when two live bots'
    /// client-id difference equals stride * attempt difference. A stride of 17
    /// collided for any cohort above 17 bots (client N+17 on its first join vs
    /// client N one rejoin later shared a bind IP, re-arming the per-IP
    /// connect throttle these binds exist to bypass). 7919 exceeds every
    /// documented cohort scale, making the map injective in practice.</summary>
    internal const int RejoinIndexStride = 7919;

    /// <summary>Bind address for one (clientId, attempt) pair, one-based on both
    /// axes so the first bot of a cohort takes 127.0.0.1. Injective while the
    /// cohort spans fewer than <see cref="RejoinIndexStride"/> consecutive
    /// client ids. Single source of truth: call sites take the address from
    /// here and must not re-derive the arithmetic (the old inline stride
    /// drifted away from its test).</summary>
    public static string LoopbackBindFor(int clientId, int attempt) =>
        LoopbackBindForIndex(
            Math.Max(0, clientId - 1) + Math.Max(0, attempt - 1) * RejoinIndexStride);

    void HandlePackage(
        ushort id,
        byte[] body,
        Options opt,
        Action<string> log,
        Action<byte[]> enqueue)
    {
        // PackageIds is always id 0 until remapped; see the content heuristic
        // gated by the stage check below.
        string? typeName = State.TryGetTypeName(id, out var mappedName) ? mappedName : null;
        if (typeName != null && opt.StateObserver != null)
        {
            try { opt.StateObserver.Observe(typeName, body); }
            catch (Exception ex)
            {
                log($"OBSERVER parse_error type={typeName} bodyLen={body.Length} error={SafeText(ex.Message)}");
            }
            // A dead events sink is evidence loss, not a parse problem: report it
            // once with the real cause (the observer latches and stays quiet).
            if (opt.StateObserver.SinkFaulted && !_observerSinkFaultLogged)
            {
                _observerSinkFaultLogged = true;
                log($"OBSERVER sink_fault events disabled error={SafeText(opt.StateObserver.SinkError)}");
            }
        }
        if (!State.EverJoined)
        {
            // After join, high-volume entity/chunk packages would flood logs for
            // hour-long runs. The noisy-type scan lives inside this pre-join
            // branch on purpose: once joined it is dead work, and the receive
            // path runs per package per bot for the whole session.
            bool noisy = typeName is "NetPackageEntityPosAndRot" or "NetPackageEntityRelPosAndRot"
                or "NetPackageEntityAliveFlags" or "NetPackageEntityStat" or "NetPackageEntityStats"
                or "NetPackagePlayerStats" or "NetPackageEntityMotion" or "NetPackageChunk"
                or "NetPackageChunkClusterInfo" or "NetPackageWaterUpdate" or "NetPackageTileEntity"
                or "NetPackageEntitySpawn" or "NetPackageEntityRemove" or "NetPackageEntityDespawn"
                or "NetPackageEntityAnimationData" or "NetPackageEntityLookAt";
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
        // clears the id table) and scramble routing mid-session. Stage gates
        // first: once mappings are in, this whole check costs one comparison
        // per package instead of a dictionary lookup on every received frame.
        if (State.Stage < JoinStage.PackageIdsReceived
            && (typeName == "NetPackagePackageIds"
                || (id == 0 && body.Length > 16
                    && !State.PackageIds.ContainsKey("NetPackagePlayerLogin"))))
        {
            try
            {
                var (ver, maps, eac) = PackageCodec.ParsePackageIdsBody(body);
                State.ServerVersion = ver;
                State.ApplyPackageMappings(maps);
                log($"STAGE PackageIdsReceived: ver={PackageCodec.VersionLongString(ver)} " +
                    $"({ver.ReleaseType}.{ver.Major}.{ver.Minor}.{ver.Build}) maps={maps.Length} eac={eac}");
                if (eac)
                    log("NOTE: serverUseEAC=true; login may be denied without EAC");
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
                log($"STAGE AuthState: {SafeText(key)}");
            }
            catch (Exception ex)
            {
                // Handshake breadcrumb, not fatal: a malformed AuthState body
                // must still be visible when the join later stalls or fails.
                log($"STAGE AuthState unreadable (bodyLen={body.Length}): {ex.Message}");
            }
            return;
        }

        if (typeName == "NetPackagePlayerLoginAnswer")
        {
            try
            {
                var (allowed, data) = PackageCodec.ParseLoginAnswerBody(body);
                State.Advance(JoinStage.LoginAnswered, $"allowed={allowed} data={SafeText(data)}");
                log($"STAGE LoginAnswered: allowed={allowed} dataLen={data?.Length ?? 0}");
                if (!allowed)
                {
                    State.Fail($"login_denied: {SafeText(data)}");
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
                opt.StateObserver?.Joined(entityId);
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
                int entityId = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0));
                State.Advance(JoinStage.PlayerIdReceived, $"entityId={entityId} bodyLen={body.Length}");
                log($"STAGE PlayerIdReceived: entityId={entityId} bodyLen={body.Length}");
                // Join-moment contract for orchestrators (compare_sut.sh waits on
                // this): unlike the session-end "PASS joined" summary, this line
                // is written the instant the bot is in the game world.
                log($"JOINED entity={entityId}");
                opt.StateObserver?.Joined(entityId);
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
            _bench?.OnRespawn();
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
    internal void TryDetectWorldDeath(string? typeName, byte[] body, Options opt, Action<string> log)
    {
        if (typeName == "NetPackageEntityStatChanged" && body.Length >= 21 && State.EntityId > 0)
        {
            int eid = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0));
            byte estat = body[8];
            float value = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(9));
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
                int eid = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0));
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

        log($"CHAT {Snippet(text, 160)}");

        // Fold normalization forms before matching: an NFD name from argv
        // ("Zoe" + combining acute) and its NFC echo from the server relay are
        // byte-different strings; ordinal matching would miss our own death.
        string lower = WorldDeathBus.NormalizeIdentity(text).ToLowerInvariant();
        string ourName = WorldDeathBus.NormalizeIdentity(opt.PlayerName + opt.ClientId).ToLowerInvariant();
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
        string snippet = Snippet(text, 120);
        log($"DEATH cause={State.DeathCause} entity={State.EntityId} via=chat text={snippet}");
    }

    // Whole-word substring match (allocation-free): "refake3" matches "refake3 died"
    // but not "refake33", so one bot's death GMSG never flips a differently-numbered bot.
    internal static bool ContainsWord(string haystack, string word)
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

    /// <summary>Scrub server-controlled handshake text (auth keys, login-answer
    /// data) before it reaches State/log lines: control characters would inject
    /// newlines or terminal escapes into line-parsed logs. Each control char is
    /// replaced by '?' so scrubbing stays visible; output bounded like chat.</summary>
    static string SafeText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(Math.Min(s.Length, 160));
        foreach (char c in s)
        {
            // Stop at the snippet cap so a hostile oversized string cannot make
            // the scrub loop itself the cost; Snippet still trims a split
            // surrogate pair exactly as before.
            if (sb.Length >= 160) break;
            sb.Append(char.IsControl(c) ? '?' : c);
        }
        return Snippet(sb.ToString(), 160);
    }

    // Allocation-free letter probe (LINQ Any allocated an enumerator plus a
    // delegate per chat/GMSG package on the joined receive path).
    static bool HasLetter(string s)
    {
        foreach (char c in s)
            if (char.IsLetter(c)) return true;
        return false;
    }

    /// <summary>Truncate for logging without splitting a surrogate pair: chat
    /// text is server-controlled and may end in emoji at the cut point.</summary>
    static string Snippet(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        int len = maxChars;
        if (char.IsHighSurrogate(s[len - 1]))
            len--;
        return s[..len];
    }

    /// <summary>Server-controlled chat/GMSG text for logging and death-word
    /// matching. Both paths neutralize control characters: a hostile server must
    /// not inject newlines or terminal escapes into line-parsed logs (the
    /// harness greps PASS/FAIL lines), while letters survive for matching.</summary>
    internal static string ExtractPrintable(byte[] body)
    {
        if (body.Length == 0) return "";
        try
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms, System.Text.Encoding.UTF8);
            if (body.Length >= 2)
            {
                string s = r.ReadString();
                if (s.Length >= 3 && HasLetter(s))
                {
                    var clean = new System.Text.StringBuilder(s.Length);
                    foreach (char c in s)
                        clean.Append(char.IsControl(c) ? '?' : c);
                    return clean.ToString();
                }
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

        // Drain (monotonic: immune to wall-clock steps during the window).
        // Do NOT call server.Poll() here: the background poller below is still
        // running, and concurrent PollEvents() on one NetManager would dispatch
        // its receive handlers on both threads at once.
        var drain = Stopwatch.StartNew();
        while (drain.ElapsedMilliseconds < 300) { Thread.Sleep(5); }
        cts.Cancel();
        try
        {
            poll.Wait(1000);
        }
        catch (AggregateException ex)
        {
            // A dead mock poller stalls the join (no challenge/login answer is
            // ever serviced); the cause must be visible instead of surfacing as
            // an unexplained 25s client timeout.
            var baseEx = ex.GetBaseException();
            log?.Invoke($"FAIL self-test poller faulted: {baseEx.GetType().Name}: {baseEx.Message}");
        }

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
