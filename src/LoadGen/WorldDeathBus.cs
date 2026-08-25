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
        // Monotonic ms-since-boot (Environment.TickCount64): producer and
        // consumer share this process, and a wall-clock step (NTP sync, VM
        // resume) must neither expire a fresh kill early nor keep a stale one.
        KilledTickMs[NormalizeIdentity(playerName.Trim())] = Environment.TickCount64;
    }

    /// <summary>True if this name was killed recently (consumes the event).</summary>
    public static bool TryConsumeKill(string playerName, out long killedAtTickMs)
    {
        killedAtTickMs = 0;
        if (string.IsNullOrWhiteSpace(playerName)) return false;
        if (!KilledTickMs.TryRemove(NormalizeIdentity(playerName.Trim()), out killedAtTickMs))
            return false;
        // Ignore stale kills older than 2 minutes
        return Environment.TickCount64 - killedAtTickMs < 120_000;
    }
}
