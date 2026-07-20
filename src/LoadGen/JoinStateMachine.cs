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

public sealed class JoinStateMachine
{
    readonly List<string> _log = new();
    public JoinStage Stage { get; private set; } = JoinStage.Created;
    public string? FailReason { get; private set; }
    public IReadOnlyList<string> Log => _log;
    public Dictionary<string, ushort> PackageIds { get; } = new(StringComparer.Ordinal);
    /// <summary>Compat version from NetPackagePackageIds (for VersionAuthorizer LongStringNoBuild).</summary>
    public PackageCodec.VersionInfo ServerVersion { get; set; } = PackageCodec.GameVersion;
    public int EntityId { get; set; } = -1;
    public float PosX { get; set; }
    public float PosY { get; set; } = 70f;
    public float PosZ { get; set; }
    public int WalkActions { get; set; }
    public int JumpActions { get; set; }
    public int CrouchActions { get; set; }
    public int AimActions { get; set; }
    public int TurnActions { get; set; }
    public int StrafeActions { get; set; }
    public int LookActions { get; set; }
    public int ChatActions { get; set; }
    public int BreakBlockActions { get; set; }
    public int AttackActions { get; set; }
    public int DrownActions { get; set; }
    public int SuicideActions { get; set; }
    public int KilledActions { get; set; }
    public bool Died { get; set; }
    /// <summary>
    /// drown_fatal | suicide | killed_external | suicide_fallback |
    /// world_death | world_killed | world_drown | world_radiation |
    /// server_disconnect | timeout_alive | none
    /// </summary>
    public string DeathCause { get; set; } = "none";
    /// <summary>How many times this bot has died (across respawns).</summary>
    public int DeathCount { get; set; }
    /// <summary>Successful respawns after a death.</summary>
    public int RespawnCount { get; set; }
    /// <summary>Waiting for server PlayerId / SpawnedInWorld after RequestToSpawnPlayer.</summary>
    public bool AwaitingRespawn { get; set; }
    /// <summary>True once we adopted the server's authoritative ground Y.</summary>
    public bool GroundAdopted { get; set; }
    /// <summary>Bounded counter for logging server position corrections.</summary>
    public int CorrectionLogged { get; set; }
    public string BotModeName { get; set; } = "Wander";
    public int PackagesReceived { get; set; }
    public int PackagesSent { get; set; }
    /// <summary>Monotonic move counter used to pace absolute-position keyframes.</summary>
    public long MoveTicks { get; set; }
    public bool SpawnRequested { get; set; }

    public bool EverJoined { get; private set; }
    public bool IsJoined => EverJoined || Stage == JoinStage.Joined || (Stage == JoinStage.SpawnedInWorld && EntityId > 0);
    /// <summary>True when the client should stop its main loop (not merely "joined").</summary>
    public bool IsTerminal => Stage is JoinStage.Failed or JoinStage.Disconnected;

    /// <summary>Clear death flags after a successful respawn so the next life can walk.</summary>
    public void ClearDeathForNewLife()
    {
        Died = false;
        DeathCause = "none";
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
    }

    public void Fail(string reason)
    {
        if (Stage is JoinStage.Failed or JoinStage.Joined) return;
        FailReason = reason;
        Stage = JoinStage.Failed;
        _log.Add($"STAGE Failed: {reason}");
    }

    public void Note(string msg) => _log.Add(msg);

    public bool TryGetPackageId(string typeName, out ushort id) =>
        PackageIds.TryGetValue(typeName, out id);

    public void ApplyPackageMappings(string[] mappings)
    {
        PackageIds.Clear();
        for (int i = 0; i < mappings.Length; i++)
        {
            if (!string.IsNullOrEmpty(mappings[i]))
                PackageIds[mappings[i]] = (ushort)i;
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
