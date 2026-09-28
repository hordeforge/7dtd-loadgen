namespace SevenDTD.LoadGen;

/// <summary>Ordered join stages for a 7DTD simulated client (fail-loud, not socket-open success).</summary>
public enum JoinStage
{
    Created = 0,
    UdpOpen,
    LiteNetStarted,
    LiteNetConnected,
    ChallengeReceived,
    ChallengeReplied,
    PackageIdsReceived,
    LoginSent,
    LoginAnswered,
    PlayerIdReceived,
    SpawnedInWorld,
    Joined,
    Failed,
    Disconnected,
}

/// <summary>What the client does with a received package, decided from its
/// type name once instead of on every frame. <see cref="PackageKinds"/> owns
/// the name-to-kind table; <see cref="JoinStateMachine.KindOf"/> answers per
/// id in O(1) from an array the reverse map fills.</summary>
public enum PackageKind
{
    /// <summary>The server has not sent PackageIds yet, or the id is not in
    /// its table (an empty mapping entry lands here too).</summary>
    Unmapped = 0,
    /// <summary>Mapped, but no handler claims it: entity motion, chunks,
    /// water, tile entities. The bulk of a joined session's traffic.</summary>
    Other,
    PackageIds,
    AuthConfirmation,
    AuthState,
    PlayerLoginAnswer,
    WorldInfo,
    WorldSpawnPoints,
    GameStats,
    PlayerSpawnedInWorld,
    PlayerId,
    PlayerDenied,
    EntityPosAndRot,
    EntityTeleport,
    EntityStatChanged,
    EntityRemove,
    EntityDespawn,
    RemoveEntity,
    EntityDestroy,
    SimpleChat,
    Chat,
    GameMessage,
    ChatMessage,
}

/// <summary>Single source of truth for the name-to-kind mapping. Both the
/// reverse-map build and any caller naming a type resolve through here, so a
/// handler and the table that routes to it cannot drift apart.</summary>
public static class PackageKinds
{
    public static PackageKind Of(string? typeName) => typeName switch
    {
        "NetPackagePackageIds" => PackageKind.PackageIds,
        "NetPackageAuthConfirmation" => PackageKind.AuthConfirmation,
        "NetPackageAuthState" => PackageKind.AuthState,
        "NetPackagePlayerLoginAnswer" => PackageKind.PlayerLoginAnswer,
        "NetPackageWorldInfo" => PackageKind.WorldInfo,
        "NetPackageWorldSpawnPoints" => PackageKind.WorldSpawnPoints,
        "NetPackageGameStats" => PackageKind.GameStats,
        "NetPackagePlayerSpawnedInWorld" => PackageKind.PlayerSpawnedInWorld,
        "NetPackagePlayerId" => PackageKind.PlayerId,
        "NetPackagePlayerDenied" => PackageKind.PlayerDenied,
        "NetPackageEntityPosAndRot" => PackageKind.EntityPosAndRot,
        "NetPackageEntityTeleport" => PackageKind.EntityTeleport,
        "NetPackageEntityStatChanged" => PackageKind.EntityStatChanged,
        "NetPackageEntityRemove" => PackageKind.EntityRemove,
        "NetPackageEntityDespawn" => PackageKind.EntityDespawn,
        "NetPackageRemoveEntity" => PackageKind.RemoveEntity,
        "NetPackageEntityDestroy" => PackageKind.EntityDestroy,
        "NetPackageSimpleChat" => PackageKind.SimpleChat,
        "NetPackageChat" => PackageKind.Chat,
        "NetPackageGameMessage" => PackageKind.GameMessage,
        "NetPackageChatMessage" => PackageKind.ChatMessage,
        null => PackageKind.Unmapped,
        _ => PackageKind.Other,
    };
}

/// <summary>Why a bot's current life ended. One enum, one meaning: the state
/// and the action loop report the same cause rather than translating between
/// parallel representations.</summary>
public enum DeathCause
{
    None = 0,
    DrownFatal,
    Suicide,
    KilledExternal,
    SuicideFallback,
    /// <summary>Server-driven death seen only as an entity removal.</summary>
    WorldDeath,
    /// <summary>Health reached zero (stat change, or a telnet kill).</summary>
    WorldKilled,
    WorldDrown,
    WorldRadiation,
    ServerDisconnect,
    TimeoutAlive,
    RespawnTimeout,
    Exception,
}

/// <summary>Stable lowercase cause names for the stats JSON, the deaths CSV and
/// the log lines. Renaming one is a report-schema change, so every name the
/// reports emit is declared here.</summary>
public static class DeathCauseNames
{
    public static string Of(DeathCause cause) => cause switch
    {
        DeathCause.DrownFatal => "drown_fatal",
        DeathCause.Suicide => "suicide",
        DeathCause.KilledExternal => "killed_external",
        DeathCause.SuicideFallback => "suicide_fallback",
        DeathCause.WorldDeath => "world_death",
        DeathCause.WorldKilled => "world_killed",
        DeathCause.WorldDrown => "world_drown",
        DeathCause.WorldRadiation => "world_radiation",
        DeathCause.ServerDisconnect => "server_disconnect",
        DeathCause.TimeoutAlive => "timeout_alive",
        DeathCause.RespawnTimeout => "respawn_timeout",
        DeathCause.Exception => "exception",
        _ => "none",
    };
}

/// <summary>Report bucket for every <see cref="DeathCause"/>, in the order
/// DEATH_STATS and the stats json emit them.
/// <para>
/// The buckets are a partition: each cause lands in exactly one, and a cause
/// the table omits lands in none, so the emitted counts stop summing to the
/// cohort total and the residual is invisible in both renderings. The list
/// used to be open-coded at its call site, where a cause nobody remembered
/// dropped out of the artifact while the console still reported it in
/// DEATH_HISTOGRAM. It is a table here for that reason:
/// <see cref="KeyOf"/> refuses an unmapped cause and
/// DeathCauseBucketTests walks the whole enum.
/// </para></summary>
public static class DeathCauseBuckets
{
    /// <summary>Bucket keys, in report order. The eight cause buckets plus
    /// <c>none</c> (a bot that finished its life alive) cover every cohort
    /// member, so the counts sum to the total.</summary>
    public static readonly string[] Keys =
    {
        "world_killed", "world_drown", "world_radiation", "timeout_alive",
        "disconnect", "self_kill", "respawn_timeout", "exception", "none",
    };

    static readonly Dictionary<DeathCause, string> ByCause = new()
    {
        [DeathCause.None] = "none",
        // Server-driven death arrives two ways (a stat change, or only an
        // entity removal) and both report as one world kill.
        [DeathCause.WorldKilled] = "world_killed",
        [DeathCause.WorldDeath] = "world_killed",
        [DeathCause.WorldDrown] = "world_drown",
        [DeathCause.WorldRadiation] = "world_radiation",
        [DeathCause.TimeoutAlive] = "timeout_alive",
        [DeathCause.ServerDisconnect] = "disconnect",
        [DeathCause.DrownFatal] = "self_kill",
        [DeathCause.Suicide] = "self_kill",
        [DeathCause.SuicideFallback] = "self_kill",
        [DeathCause.KilledExternal] = "self_kill",
        [DeathCause.RespawnTimeout] = "respawn_timeout",
        [DeathCause.Exception] = "exception",
    };

    /// <summary>Report key for a cause. Throws rather than returning a
    /// sentinel: an unmapped cause is a table bug, and a sentinel would put
    /// it in no bucket and quietly break the partition the report promises.</summary>
    public static string KeyOf(DeathCause cause) =>
        ByCause.TryGetValue(cause, out var key)
            ? key
            : throw new InvalidOperationException(
                $"death cause {cause} has no report bucket; add it to DeathCauseBuckets");
}

public sealed class JoinStateMachine
{
    // Soak cohorts log continuously (walk/turn/chat/unparsed-frame lines) for
    // hours; nothing reads mid-session history and only --self-test-join dumps
    // Log at exit (those runs stay far below the cap). Keep the newest lines so
    // a 1000-bot multi-hour run cannot retain hundreds of MB of strings.
    const int MaxLogLines = 4000;
    const int LogTrimToLines = 2000;
    readonly List<string> _log = new();
    public JoinStage Stage { get; private set; } = JoinStage.Created;
    public string? FailReason { get; private set; }
    public IReadOnlyList<string> Log => _log;
    public Dictionary<string, ushort> PackageIds { get; } = new(StringComparer.Ordinal);
    /// <summary>Reverse of <see cref="PackageIds"/> so the per-package type
    /// lookup on the receive hot path is O(1) instead of scanning every mapping.</summary>
    readonly Dictionary<ushort, string> _typeNamesById = new();
    /// <summary>What the client does with each id, indexed by id. The receive
    /// path runs per package per bot for a whole session, so routing by a
    /// twenty-way string comparison chain on the resolved name was work done
    /// once per frame that this table does once per session. Ids the server
    /// left empty are <see cref="PackageKind.Unmapped"/>, matching
    /// <see cref="TryGetTypeName"/>'s miss.</summary>
    PackageKind[] _kindsById = Array.Empty<PackageKind>();
    /// <summary>Compat version from NetPackagePackageIds (for VersionAuthorizer LongStringNoBuild).</summary>
    public PackageCodec.VersionInfo ServerVersion { get; set; } = PackageCodec.GameVersion;
    public int EntityId { get; set; } = -1;
    public float PosX { get; set; }
    public float PosY { get; set; } = 70f;
    public float PosZ { get; set; }
    /// <summary>Every folded per-session counter as one value: the rejoin
    /// aggregation operates on the whole struct, so a counter cannot be added
    /// to the state but silently dropped from a fold body. A new counter is
    /// declared here once, exposed through one passthrough property.</summary>
    public struct LifeCounters
    {
        public int WalkActions;
        public int JumpActions;
        public int CrouchActions;
        public int AimActions;
        public int TurnActions;
        public int StrafeActions;
        public int LookActions;
        public int ChatActions;
        public int BreakBlockActions;
        public int DynamiteActions;
        public int AttackActions;
        public int DrownActions;
        public int SuicideActions;
        public int KilledActions;
        /// <summary>How many times this bot has died (across respawns).</summary>
        public int DeathCount;
        /// <summary>Successful respawns after a death.</summary>
        public int RespawnCount;
        /// <summary>Rejoin attempts after an early disconnect (retries past the first).</summary>
        public int RejoinCount;

        /// <summary>Sum another attempt's counters into this aggregate
        /// (the orchestrator folds every rejoin attempt into one report line).
        /// Attempt states always carry RejoinCount 0 - only the session total
        /// increments it directly - so folding every field is safe.</summary>
        public void Add(LifeCounters o)
        {
            WalkActions += o.WalkActions;
            JumpActions += o.JumpActions;
            CrouchActions += o.CrouchActions;
            AimActions += o.AimActions;
            TurnActions += o.TurnActions;
            StrafeActions += o.StrafeActions;
            LookActions += o.LookActions;
            ChatActions += o.ChatActions;
            BreakBlockActions += o.BreakBlockActions;
            DynamiteActions += o.DynamiteActions;
            AttackActions += o.AttackActions;
            DrownActions += o.DrownActions;
            SuicideActions += o.SuicideActions;
            KilledActions += o.KilledActions;
            DeathCount += o.DeathCount;
            RespawnCount += o.RespawnCount;
            RejoinCount += o.RejoinCount;
        }
    }

    LifeCounters _counters;

    // Public counter surface unchanged: passthrough accessors keep call sites,
    // CSV columns, and tests on named properties while folds operate on the
    // single value above.
    public int WalkActions { get => _counters.WalkActions; set => _counters.WalkActions = value; }
    public int JumpActions { get => _counters.JumpActions; set => _counters.JumpActions = value; }
    public int CrouchActions { get => _counters.CrouchActions; set => _counters.CrouchActions = value; }
    public int AimActions { get => _counters.AimActions; set => _counters.AimActions = value; }
    public int TurnActions { get => _counters.TurnActions; set => _counters.TurnActions = value; }
    public int StrafeActions { get => _counters.StrafeActions; set => _counters.StrafeActions = value; }
    public int LookActions { get => _counters.LookActions; set => _counters.LookActions = value; }
    public int ChatActions { get => _counters.ChatActions; set => _counters.ChatActions = value; }
    public int BreakBlockActions { get => _counters.BreakBlockActions; set => _counters.BreakBlockActions = value; }
    public int DynamiteActions { get => _counters.DynamiteActions; set => _counters.DynamiteActions = value; }
    public int AttackActions { get => _counters.AttackActions; set => _counters.AttackActions = value; }
    public int DrownActions { get => _counters.DrownActions; set => _counters.DrownActions = value; }
    public int SuicideActions { get => _counters.SuicideActions; set => _counters.SuicideActions = value; }
    public int KilledActions { get => _counters.KilledActions; set => _counters.KilledActions = value; }
    public int DeathCount { get => _counters.DeathCount; set => _counters.DeathCount = value; }
    public int RespawnCount { get => _counters.RespawnCount; set => _counters.RespawnCount = value; }
    public int RejoinCount { get => _counters.RejoinCount; set => _counters.RejoinCount = value; }
    /// <summary>Why the current life ended; <see cref="DeathCause.None"/>
    /// while the bot is alive. Reports render it through
    /// <see cref="DeathCauseNames.Of"/>.</summary>
    public DeathCause DeathCause { get; set; } = DeathCause.None;
    /// <summary>False while the bot is alive in its current life.</summary>
    public bool Died { get; set; }
    /// <summary>Waiting for server PlayerId / SpawnedInWorld after RequestToSpawnPlayer.</summary>
    public bool AwaitingRespawn { get; set; }
    /// <summary>True once we adopted the server's authoritative ground Y.</summary>
    public bool GroundAdopted { get; set; }
    /// <summary>Bounded counter for logging server position corrections.</summary>
    public int CorrectionLogged { get; set; }
    public string BotModeName { get; set; } = "Wander";
    public int PackagesReceived { get; set; }
    public int PackagesSent { get; set; }
    /// <summary>Milliseconds from this attempt's connect request to the
    /// server-confirmed spawn, or -1 when it never joined. Join handshake time
    /// is the latency this tool exists to measure, so the run reports it per
    /// bot and as cohort percentiles.</summary>
    public int JoinMs { get; set; } = -1;
    /// <summary>Monotonic move counter used to pace absolute-position keyframes.</summary>
    public long MoveTicks { get; set; }
    public bool SpawnRequested { get; set; }

    public bool EverJoined { get; private set; }
    public bool IsJoined => EverJoined || Stage == JoinStage.Joined || (Stage == JoinStage.SpawnedInWorld && EntityId > 0);
    /// <summary>True when the client should stop its main loop (not merely "joined").</summary>
    public bool IsTerminal => Stage is JoinStage.Failed or JoinStage.Disconnected;

    /// <summary>Sum another state's per-session counters into this aggregate
    /// (the orchestrator folds every rejoin attempt into one report line).</summary>
    public void AddCounters(JoinStateMachine other) => _counters.Add(other._counters);

    /// <summary>Overwrite this state's counters with aggregate totals so the
    /// final snapshot reports the whole session, not just the last attempt.</summary>
    public void SetCounters(JoinStateMachine totals) => _counters = totals._counters;

    /// <summary>Clear death flags after a successful respawn so the next life can walk.</summary>
    public void ClearDeathForNewLife()
    {
        Died = false;
        DeathCause = DeathCause.None;
        AwaitingRespawn = false;
        GroundAdopted = false;
    }

    public void Advance(JoinStage next, string? detail = null)
    {
        if (Stage is JoinStage.Failed or JoinStage.Disconnected) return;
        // Allow same stage detail notes; never regress except terminal fail/disconnect
        if ((int)next < (int)Stage && next is not (JoinStage.Failed or JoinStage.Disconnected))
            return;
        Stage = next;
        string line = detail == null ? $"STAGE {next}" : $"STAGE {next}: {detail}";
        _log.Add(line);
        TrimLog();
    }

    public void Fail(string reason)
    {
        if (Stage is JoinStage.Failed or JoinStage.Joined) return;
        FailReason = reason;
        Stage = JoinStage.Failed;
        _log.Add($"STAGE Failed: {reason}");
        TrimLog();
    }

    public void Note(string msg)
    {
        _log.Add(msg);
        TrimLog();
    }

    void TrimLog()
    {
        if (_log.Count > MaxLogLines)
            _log.RemoveRange(0, _log.Count - LogTrimToLines);
    }

    public bool TryGetPackageId(string typeName, out ushort id) =>
        PackageIds.TryGetValue(typeName, out id);

    /// <summary>O(1) package-id → type-name lookup for the receive path.</summary>
    public bool TryGetTypeName(ushort id, out string typeName)
    {
        if (_typeNamesById.TryGetValue(id, out var name))
        {
            typeName = name;
            return true;
        }
        typeName = "";
        return false;
    }

    /// <summary>Type name for a log line that has no other source for it, or
    /// "" when the id is not in the table.</summary>
    public string TypeNameOrEmpty(ushort id) =>
        _typeNamesById.TryGetValue(id, out var name) ? name : "";

    /// <summary>What the client does with a received id, in O(1) and without
    /// a dictionary probe. <see cref="PackageKind.Unmapped"/> covers both an
    /// id past the end of the table and one the server left empty.</summary>
    public PackageKind KindOf(ushort id) =>
        id < _kindsById.Length ? _kindsById[id] : PackageKind.Unmapped;

    public void ApplyPackageMappings(string[] mappings)
    {
        PackageIds.Clear();
        for (int i = 0; i < mappings.Length; i++)
        {
            if (!string.IsNullOrEmpty(mappings[i]))
                PackageIds[mappings[i]] = (ushort)i;
        }
        // Rebuild from the final forward map so the reverse view always agrees
        // with it (duplicate names keep the last index on both sides).
        _typeNamesById.Clear();
        _kindsById = new PackageKind[mappings.Length];
        foreach (var kv in PackageIds)
        {
            _typeNamesById[kv.Value] = kv.Key;
            _kindsById[kv.Value] = PackageKinds.Of(kv.Key);
        }
        Advance(JoinStage.PackageIdsReceived, $"count={mappings.Length}");
    }

    public void MarkJoined()
    {
        if (EntityId < 0)
            EntityId = 1; // server may not send id before spawn in all paths
        EverJoined = true;
        Advance(JoinStage.Joined, $"entityId={EntityId} pos=({PosX:0.##},{PosY:0.##},{PosZ:0.##})");
    }
}
