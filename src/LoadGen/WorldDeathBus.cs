using System.Collections.Concurrent;
using System.Text;

namespace SevenDTD.LoadGen;

/// <summary>
/// In-process side channel: telnet server kills do not always push EntityStatChanged
/// to lite clients. When the spawn task issues <c>kill PlayerName</c>, it records
/// the name here so the matching bot can treat it as world death and respawn.
/// </summary>
public static class WorldDeathBus
{
    static readonly ConcurrentDictionary<string, long> KilledTickMs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A kill older than this is stale and must not respawn anyone.</summary>
    public const long KillTtlMs = 120_000;

    /// <summary>Monotonic ms-since-boot source. Real runs use the process
    /// clock; tests substitute a virtual one so the TTL boundary is reachable
    /// without waiting two minutes.</summary>
    internal static Func<long> MonotonicMs { get; set; } = () => Environment.TickCount64;

    /// <summary>Test seam: drop pending kills and restore the real clock.</summary>
    internal static void ResetForTests()
    {
        KilledTickMs.Clear();
        MonotonicMs = () => Environment.TickCount64;
    }

    /// <summary>
    /// Identity comparison form for player names on the death path (this bus
    /// and chat-based detection): Unicode NFC. The argv-configured bot name
    /// and the server's echo of it (chat GMSG, telnet listplayers rows) can
    /// carry different normalization forms - an operator shell hands us NFD,
    /// the server relays NFC - so ordinal matching must fold both sides first
    /// or "Zoe+combining-acute" never matches its own composed echo, deaths go
    /// undetected, and the respawn loop never fires.
    /// </summary>
    internal static string NormalizeIdentity(string playerName)
        => playerName.Normalize(NormalizationForm.FormC);

    public static void NotifyKilled(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return;
        // Monotonic ms-since-boot: producer and consumer share this process,
        // and a wall-clock step (NTP sync, VM resume) must neither expire a
        // fresh kill early nor keep a stale one.
        long now = MonotonicMs();
        EvictExpired(now);
        KilledTickMs[NormalizeIdentity(playerName.Trim())] = now;
    }

    /// <summary>Drop kills past <see cref="KillTtlMs"/> whose consumer never
    /// arrived. Only TryConsumeKill removes an entry, so a kill for a bot that
    /// disconnected, finished its life, or never polled again stayed in the map
    /// for the life of the process: a wave that kills the whole cohort leaves
    /// one dead entry per name behind, every wave. The table is keyed by player
    /// name and stays bounded by the cohort, but the entries it holds are all
    /// past their TTL by construction, so nothing can ever consume them.</summary>
    /// <remarks>A negative age (a stamp written by a clock that jumped
    /// forward of the current reading) is not an expired kill and is kept.</remarks>
    static void EvictExpired(long now)
    {
        foreach (var (name, killedAt) in KilledTickMs)
            if (now - killedAt >= KillTtlMs)
                KilledTickMs.TryRemove(name, out _);
    }

    /// <summary>True if this name was killed recently (consumes the event).</summary>
    public static bool TryConsumeKill(string playerName, out long killedAtTickMs)
    {
        killedAtTickMs = 0;
        if (string.IsNullOrWhiteSpace(playerName)) return false;
        if (!KilledTickMs.TryRemove(NormalizeIdentity(playerName.Trim()), out killedAtTickMs))
            return false;
        return MonotonicMs() - killedAtTickMs < KillTtlMs;
    }
}
