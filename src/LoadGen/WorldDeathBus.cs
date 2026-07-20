using System.Collections.Concurrent;

namespace SevenDTD.LoadGen;

/// <summary>
/// In-process side channel: telnet server kills do not always push EntityStatChanged
/// to lite clients. When the spawn task issues <c>kill PlayerName</c>, it records
/// the name here so the matching bot can treat it as world death and respawn.
/// </summary>
public static class WorldDeathBus
{
    static readonly ConcurrentDictionary<string, long> KilledUtcMs = new(StringComparer.OrdinalIgnoreCase);

    public static void NotifyKilled(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return;
        KilledUtcMs[playerName.Trim()] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>True once if this name was killed recently (consumes the event).</summary>
    public static bool TryConsumeKill(string playerName, out long killedAtUtcMs)
    {
        killedAtUtcMs = 0;
        if (string.IsNullOrWhiteSpace(playerName)) return false;
        if (!KilledUtcMs.TryRemove(playerName.Trim(), out killedAtUtcMs))
            return false;
        // Ignore stale kills older than 2 minutes
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return now - killedAtUtcMs < 120_000;
    }
}
