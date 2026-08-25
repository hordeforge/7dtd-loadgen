using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SevenDTD.LoadGen;

/// <summary>
/// Minimal 7DTD dedicated telnet client (spawn zombies near live players).
/// Height-test worlds have empty prefabs, so EnemySpawnMode alone still yields Zom:0.
/// </summary>
public sealed partial class TelnetAdmin : IDisposable
{
    readonly string _host;
    readonly int _port;
    readonly string _password;
    readonly Action<string>? _log;
    TcpClient? _tcp;
    NetworkStream? _stream;
    readonly StringBuilder _buf = new();
    readonly Utf8ChunkDecoder _decoder = new();

    public TelnetAdmin(string host, int port, string password, Action<string>? log = null)
    {
        _host = host;
        _port = port;
        _password = password;
        _log = log;
    }

    // listplayers output scales with cohort size (one row per player) and is
    // re-parsed on every pressure wave; source-generated regexes keep that
    // scan compiled instead of re-interpreting the pattern per call.
    [GeneratedRegex(@"id\s*=\s*(\d+),.*?health\s*=\s*(\d+).*?pltfmid\s*=\s*Local_([^,\s]+).*?ip\s*=\s*127\.",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LivePlayerRowRegex();

    [GeneratedRegex(@"id\s*=\s*(\d+).*?Local_(REFake\d+).*?ip=127",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FallbackPlayerRowRegex();

    [GeneratedRegex(@"id\s*=\s*(\d+),.*?health\s*=\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PlayerIdHealthRegex();

    // Allowlist for tokens replayed into admin commands. The kill targets come
    // from listplayers output (server-controlled text), so a crafted row must
    // never become a crafted command: only lab bot/platform id shapes pass.
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex CommandTokenRegex();

    /// <summary>True when <paramref name="value"/> is a safe single token for
    /// interpolation into an admin command (no whitespace, quotes, separators,
    /// or control characters).</summary>
    internal static bool IsSafeCommandToken(string value) => CommandTokenRegex().IsMatch(value);

    /// <summary>
    /// The console is line-oriented: a CR/LF/NUL smuggled inside any
    /// interpolated string would terminate the command and let the remainder
    /// run as a separate admin command (threat model R3). Every outbound
    /// command passes through here, so one guard covers all current and future
    /// call sites; no legitimate command contains control characters.
    /// </summary>
    internal static bool IsSingleLineCommand(string cmd)
    {
        foreach (char c in cmd)
        {
            if (c < ' ' || c == '\x7f') return false;
        }
        return true;
    }

    public bool Connect(int timeoutMs = 5000)
    {
        Dispose();
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            var ar = _tcp.BeginConnect(_host, _port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                _log?.Invoke($"TELNET connect timeout {_host}:{_port}");
                Dispose();
                return false;
            }
            _tcp.EndConnect(ar);
            _stream = _tcp.GetStream();
            _stream.ReadTimeout = 2000;
            _stream.WriteTimeout = 2000;
            string banner = ReadAvailable(800);
            if (banner.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                // A multi-line password is broken configuration; sending it
                // would leak the tail as unauthenticated console commands.
                if (!IsSingleLineCommand(_password))
                {
                    _log?.Invoke("TELNET rejected password with control characters");
                    Dispose();
                    return false;
                }
                WriteLine(_password);
                _ = ReadAvailable(600);
            }
            _log?.Invoke($"TELNET connected {_host}:{_port}");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"TELNET connect fail: {ex.Message}");
            Dispose();
            return false;
        }
    }

    public string Exec(string cmd)
    {
        if (_stream == null || _tcp is not { Connected: true }) return "";
        if (!IsSingleLineCommand(cmd))
        {
            // Server-derived text (listplayers tokens) must never split into a
            // second admin command; drop the whole command instead.
            _log?.Invoke($"TELNET rejected non-single-line command (len={cmd.Length})");
            return "";
        }
        try
        {
            WriteLine(cmd);
            return ReadAvailable(500);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"TELNET exec fail: {ex.Message}");
            return "";
        }
    }

    /// <summary>When true, if se fails (no spawn point), fall back to server kill.</summary>
    public bool KillFallback { get; set; } = true;

    /// <summary>
    /// Apply world pressure: try zombie se first; optional kill fallback for worlds
    /// without AI spawn points (empty height-test maps).
    /// </summary>
    public int SpawnZombiesNearPlayers(string entityName = "zombieBoe", int perPlayer = 3)
    {
        string outp = Exec("listplayers");
        // Prefer living connected bots only (skip health=0 leftovers from prior kills).
        var live = new List<(int id, string name)>();
        foreach (Match m in LivePlayerRowRegex().Matches(outp))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id) || id <= 0) continue;
            if (!int.TryParse(m.Groups[2].Value, out int hp) || hp <= 0) continue;
            string token = m.Groups[3].Value;
            if (!IsSafeCommandToken(token))
            {
                // Crafted/malformed row: never feed it back into kill/give.
                _log?.Invoke($"TELNET skipped unsafe player token (len={token.Length})");
                continue;
            }
            live.Add((id, token));
        }
        if (live.Count == 0)
        {
            // Loose fallback
            foreach (Match m in FallbackPlayerRowRegex().Matches(outp))
            {
                if (int.TryParse(m.Groups[1].Value, out int id) && id > 0)
                    live.Add((id, m.Groups[2].Value));
            }
        }
        var ids = live.Select(x => x.id).Distinct().ToList();
        var names = live.Select(x => x.name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        int spawned = 0;
        bool anySpawnPoint = false;
        // Prefer AIDirector scouts (works on Navezgane even when se can't find a grid cell).
        foreach (int id in ids.Take(8))
        {
            string r = Exec($"spawnscouts {id}");
            if (r.Contains("Spawned", StringComparison.OrdinalIgnoreCase)
                || r.Contains("Spawning this wave", StringComparison.OrdinalIgnoreCase)
                || r.Contains("scout horde", StringComparison.OrdinalIgnoreCase)
                || r.Contains("Scouts spawning", StringComparison.OrdinalIgnoreCase)
                || r.Contains("scout", StringComparison.OrdinalIgnoreCase))
            {
                anySpawnPoint = true;
                spawned += 2;
            }
        }

        // Exact class selection: --spawn-entity takes a comma list of entity
        // classes (README) and spawns exactly those. Padding a single name with
        // default zombies would put zombies into a vehicles-only pressure request.
        string[] types = string.IsNullOrWhiteSpace(entityName)
            ? new[] { "zombieBoe", "zombieSteve", "zombieArlene" }
            : entityName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (int id in ids)
        {
            // Bounded per round; callers scale rounds via spawn-every-ms for hundreds total.
            for (int i = 0; i < Math.Min(perPlayer, 25); i++)
            {
                string type = types[i % types.Length];
                string r = Exec($"spawnentity {id} {type}");
                if (r.Contains("No spawn point", StringComparison.OrdinalIgnoreCase))
                    break;
                if (r.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    && r.Contains("Player", StringComparison.OrdinalIgnoreCase))
                    break;
                anySpawnPoint = true;
                spawned++;
            }
        }

        // Height-test / broken ground: se says "No spawn point found near player".
        // Optional kill so death+respawn still exercises on those maps.
        int killed = 0;
        if (!anySpawnPoint && KillFallback && names.Count > 0)
        {
            foreach (string name in names)
            {
                string r = Exec($"kill {name}");
                if (r.Contains("damage", StringComparison.OrdinalIgnoreCase)
                    || r.Contains("Gave", StringComparison.OrdinalIgnoreCase))
                {
                    killed++;
                    WorldDeathBus.NotifyKilled(name);
                }
            }
            _log?.Invoke(
                $"TELNET world_kill players={names.Count} killed={killed} " +
                $"(se spawn-point missing on this world)");
        }
        else if (!anySpawnPoint && !KillFallback)
            _log?.Invoke($"TELNET se/scouts failed (no spawn point); kill fallback off, livePlayers={ids.Count}");
        else if (spawned > 0 || ids.Count > 0)
            _log?.Invoke($"TELNET pressure livePlayers={ids.Count} units~={spawned} type={entityName}");
        return spawned + killed;
    }

    // Rotating index so successive hordes target different areas of the cohort.
    int _hordeCursor;

    /// <summary>Spawn a wandering horde: a concentrated scout-horde burst aimed at
    /// a rotating subset of players. Scouts spawn at distance and path in as a
    /// group, exercising long-range pathfinding, group cohesion, and the spawn
    /// manager - distinct from the steady spawn-on-player trickle. `waves` scout
    /// calls per targeted player; `targets` players per horde.</summary>
    public int SpawnWanderingHorde(int waves = 3, int targets = 2)
    {
        string outp = Exec("listplayers");
        var ids = new List<int>();
        foreach (Match m in PlayerIdHealthRegex().Matches(outp))
        {
            if (int.TryParse(m.Groups[1].Value, out int id) && id > 0
                && int.TryParse(m.Groups[2].Value, out int hp) && hp > 0)
                ids.Add(id);
        }
        if (ids.Count == 0) return 0;
        int spawned = 0;
        int hit = Math.Min(targets, ids.Count);
        for (int t = 0; t < hit; t++)
        {
            int id = ids[(_hordeCursor + t) % ids.Count];
            for (int w = 0; w < Math.Max(1, waves); w++)
            {
                string r = Exec($"spawnscouts {id}");
                if (r.Contains("scout", StringComparison.OrdinalIgnoreCase)
                    || r.Contains("Spawn", StringComparison.OrdinalIgnoreCase))
                    spawned += 4;
            }
        }
        // Advance by the number actually targeted (not requested), so rotation
        // stays even when targets > player count.
        _hordeCursor = (_hordeCursor + hit) % ids.Count;
        _log?.Invoke($"TELNET wandering_horde targets={hit} waves={waves} units~={spawned}");
        return spawned;
    }

    void WriteLine(string s)
    {
        if (_stream == null) return;
        // The server telnet speaks UTF-8 (see ReadAvailable); commands echo
        // player names parsed from its output, so ASCII here would corrupt any
        // non-ASCII name (kill Zöé -> kill Zo?e).
        byte[] data = Encoding.UTF8.GetBytes(s + "\n");
        _stream.Write(data, 0, data.Length);
        _stream.Flush();
    }

    string ReadAvailable(int waitMs)
    {
        if (_stream == null) return "";
        // Monotonic window: a wall-clock step (NTP correction) mid-read must
        // not cut the wait short or stretch it past waitMs.
        var sw = Stopwatch.StartNew();
        var tmp = new byte[4096];
        while (sw.ElapsedMilliseconds < waitMs)
        {
            try
            {
                if (_stream.DataAvailable)
                {
                    int n = _stream.Read(tmp, 0, tmp.Length);
                    if (n > 0) _buf.Append(_decoder.Decode(tmp.AsSpan(0, n)));
                }
                else
                    Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                // The read window ends early on an IO fault; leave the same
                // breadcrumb Exec's failure path leaves so empty responses are
                // attributable to the dropped session instead of a silent server.
                _log?.Invoke($"TELNET read fail: {ex.Message}");
                break;
            }
        }
        string all = _buf.ToString();
        if (_buf.Length > 8000)
        {
            _buf.Remove(0, _buf.Length - 4000);
            DropUnpairedRingHead(_buf);
        }
        return all;
    }

    /// <summary>After a ring cut the retained window can begin inside a
    /// surrogate pair (chat text with emoji at the cut point): the cut either
    /// keeps only a trail half (leading lone low surrogate) or splits before
    /// the lead. Drop an unpaired half so the window stays well-formed UTF-16.</summary>
    internal static void DropUnpairedRingHead(StringBuilder buf)
    {
        if (buf.Length == 0) return;
        char c0 = buf[0];
        bool unpaired = char.IsHighSurrogate(c0)
            ? buf.Length == 1 || !char.IsLowSurrogate(buf[1])
            : char.IsLowSurrogate(c0);
        if (unpaired)
            buf.Remove(0, 1);
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Close(); } catch { /* ignore */ }
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
        _buf.Clear();
        _decoder.Reset();
    }
}
