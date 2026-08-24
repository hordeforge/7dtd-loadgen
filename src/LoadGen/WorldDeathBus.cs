using System.Collections.Concurrent;

namespace SevenDTD.LoadGen;

/// <summary>
/// In-process side channel: telnet server kills do not always push EntityStatChanged
/// to lite clients. When the spawn task issues <c>kill PlayerName</c>, it records
/// the name here so the matching bot can treat it as world death and respawn.
/// </summary>
public static class WorldDeathBus
{
    static readonly ConcurrentDictionary<string, long> KilledTickMs = new(StringComparer.OrdinalIgnoreCase);

    public static void NotifyKilled(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return;
        // Monotonic ms-since-boot (Environment.TickCount64): producer and
        // consumer share this process, and a wall-clock step (NTP sync, VM
        // resume) must neither expire a fresh kill early nor keep a stale one.
        KilledTickMs[playerName.Trim()] = Environment.TickCount64;
    }

    /// <summary>True once if this name was killed recently (consumes the event).</summary>
    public static bool TryConsumeKill(string playerName, out long killedAtTickMs)
    {
        killedAtTickMs = 0;
        if (string.IsNullOrWhiteSpace(playerName)) return false;
        if (!KilledTickMs.TryRemove(playerName.Trim(), out killedAtTickMs))
            return false;
        // Ignore stale kills older than 2 minutes
        return Environment.TickCount64 - killedAtTickMs < 120_000;
    }
}
