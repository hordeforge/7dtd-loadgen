using System.Collections.Concurrent;
using LiteNetLib;

namespace SevenDTD.LoadGen;

/// <summary>
/// Minimal LiteNetLib server that speaks challenge-auth + PackageIds + login answer + spawn,
/// and counts walk/jump packages. Used for automated join+action tests without 7DTD.
/// </summary>
public sealed class MockGameServer : IDisposable
{
    static readonly string[] DefaultMappings =
    {
        "NetPackagePackageIds",
        "NetPackagePlayerLogin",
        "NetPackagePlayerLoginAnswer",
        "NetPackagePlayerId",
        "NetPackagePlayerSpawnedInWorld",
        "NetPackageRequestToSpawnPlayer",
        "NetPackageEntityPosAndRot",
        "NetPackageEntityRelPosAndRot",
        "NetPackageEntityAliveFlags",
        "NetPackageDamageEntity",
        "NetPackageSimpleChat",
        "NetPackageEntityLookAt",
    };

    readonly EventBasedNetListener _listener = new();
    readonly NetManager _net;
    readonly ConcurrentDictionary<int, Guid> _challenges = new();
    readonly ConcurrentDictionary<int, bool> _authed = new();
    int _nextEntity = 100;

    // Counters are bumped inside LiteNetLib event handlers, which run on
    // whichever thread calls Poll(); increment them atomically so two pollers
    // can never lose an update. Reads are plain (atomic) int loads.
    int _walkPackages;
    int _jumpPackages;
    int _drownPackages;
    int _suicidePackages;
    int _killPackages;
    int _chatPackages;
    int _flagPackages;
    int _lookPackages;
    int _loginsAccepted;
    int _challengesSent;
    int _challengesOk;

    public int Port { get; private set; }
    public int WalkPackages => _walkPackages;
    public int JumpPackages => _jumpPackages;
    public int DrownPackages => _drownPackages;
    public int SuicidePackages => _suicidePackages;
    public int KillPackages => _killPackages;
    public int ChatPackages => _chatPackages;
    public int FlagPackages => _flagPackages;
    public int LookPackages => _lookPackages;
    public int LoginsAccepted => _loginsAccepted;
    public int ChallengesSent => _challengesSent;
    public int ChallengesOk => _challengesOk;

    public MockGameServer()
    {
        _net = new NetManager(_listener)
        {
            AutoRecycle = true,
            UpdateTime = 15,
        };
        _listener.ConnectionRequestEvent += req => req.Accept();
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.PeerDisconnectedEvent += (p, info) =>
        {
            _challenges.TryRemove(p.Id, out Guid _);
            _authed.TryRemove(p.Id, out bool _);
            _ = info;
        };
    }

    public void Start(int port = 0)
    {
        if (port <= 0)
        {
            if (!_net.Start())
                throw new InvalidOperationException("MockGameServer Start(0) failed");
            Port = _net.LocalPort;
        }
        else
        {
            if (!_net.Start(port))
                throw new InvalidOperationException($"MockGameServer Start({port}) failed");
            Port = port;
        }
    }

    public void Poll() => _net.PollEvents();

    void OnPeerConnected(NetPeer peer)
    {
        var challenge = Guid.NewGuid();
        _challenges[peer.Id] = challenge;
        var buf = new byte[PackageCodec.ChallengeSize];
        buf[0] = PackageCodec.ChallengeChannelMarker;
        challenge.ToByteArray().CopyTo(buf, 1);
        peer.Send(buf, DeliveryMethod.ReliableOrdered);
        Interlocked.Increment(ref _challengesSent);
    }

    void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        int n = reader.AvailableBytes;
        if (n <= 0) { reader.Recycle(); return; }
        var data = new byte[n];
        reader.GetBytes(data, n);
        reader.Recycle();

        if (!_authed.ContainsKey(peer.Id))
        {
            if (n == PackageCodec.ChallengeSize
                && data[0] == PackageCodec.ChallengeChannelMarker
                && _challenges.TryGetValue(peer.Id, out var expected))
            {
                var got = new Guid(data.AsSpan(1, 16));
                if (got == expected)
                {
                    Interlocked.Increment(ref _challengesOk);
                    _authed[peer.Id] = true;
                    // PackageIds (id 0) with full LiteNet envelope (channel + size/comp/enc/count)
                    var pkg = PackageCodec.BuildPackageIdsServer(
                        0, DefaultMappings, PackageCodec.GameVersion, serverUseEac: false);
                    peer.Send(pkg, DeliveryMethod.ReliableOrdered);
                }
                else
                {
                    peer.Disconnect();
                }
            }
            return;
        }

        // Authenticated: parse full LiteNet game envelope (channel + packages)
        foreach (var (id, body) in PackageCodec.ParseChannelPayload(data))
        {
            string? name = id < DefaultMappings.Length ? DefaultMappings[id] : null;
            if (name == "NetPackagePlayerLogin")
            {
                Interlocked.Increment(ref _loginsAccepted);
                int entityId = Interlocked.Increment(ref _nextEntity);
                // LoginAnswer id=2
                peer.Send(
                    PackageCodec.BuildPlayerLoginAnswer(2, allowed: true, data: "ok"),
                    DeliveryMethod.ReliableOrdered);
                // Spawned id=4
                peer.Send(
                    PackageCodec.BuildPlayerSpawnedInWorld(4, respawnReason: 0, 256, 70, 256, entityId),
                    DeliveryMethod.ReliableOrdered);
            }
            else if (name == "NetPackageRequestToSpawnPlayer")
            {
                // Respawn path: new entity + SpawnedInWorld
                int entityId = Interlocked.Increment(ref _nextEntity);
                peer.Send(
                    PackageCodec.BuildPlayerSpawnedInWorld(4, respawnReason: 1, 256, 70, 256, entityId),
                    DeliveryMethod.ReliableOrdered);
            }
            else if (name == "NetPackageEntityPosAndRot" || name == "NetPackageEntityRelPosAndRot")
            {
                Interlocked.Increment(ref _walkPackages);
            }
            else if (name == "NetPackageEntityAliveFlags")
            {
                Interlocked.Increment(ref _flagPackages);
                var (_, flags) = PackageCodec.ParseAliveFlagsBody(body);
                if ((flags & PackageCodec.FlagJumping) != 0)
                    Interlocked.Increment(ref _jumpPackages);
                else
                    Interlocked.Increment(ref _walkPackages);
            }
            else if (name == "NetPackageSimpleChat" || name == "NetPackageChat")
            {
                Interlocked.Increment(ref _chatPackages);
            }
            else if (name == "NetPackageEntityLookAt")
            {
                Interlocked.Increment(ref _lookPackages);
            }
            else if (name == "NetPackageDamageEntity" && body.Length >= 8)
            {
                // entityId i32, src u8, typ u8, ...
                byte src = body[4];
                byte typ = body[5];
                if (typ == PackageCodec.DamageTypeSuicide)
                    Interlocked.Increment(ref _suicidePackages);
                else if (typ == PackageCodec.DamageTypeSuffocation)
                    Interlocked.Increment(ref _drownPackages);
                else if (src == PackageCodec.DamageSourceExternal)
                    Interlocked.Increment(ref _killPackages);
            }
        }
    }

    public void Dispose()
    {
        // Same guard as every other stop site: release must never mask the
        // caller's result (a throw inside using-dispose would overwrite rc).
        try { _net.Stop(); }
        catch { /* ignore */ }
    }
}
