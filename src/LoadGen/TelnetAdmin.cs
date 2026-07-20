using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SevenDTD.LoadGen;

/// <summary>
/// Minimal 7DTD dedicated telnet client (spawn zombies near live players).
/// Height-test worlds have empty prefabs, so EnemySpawnMode alone still yields Zom:0.
/// </summary>
public sealed class TelnetAdmin : IDisposable
{
    readonly string _host;
    readonly int _port;
    readonly string _password;
    readonly Action<string>? _log;
    TcpClient? _tcp;
    NetworkStream? _stream;
    readonly StringBuilder _buf = new();

    public TelnetAdmin(string host, int port, string password, Action<string>? log = null)
    {
        _host = host;
        _port = port;
        _password = password;
        _log = log;
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
            if (banner.Contains("password", StringComparison.OrdinalIgnoreCase)
                || banner.Contains("Password", StringComparison.Ordinal))
            {
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
        foreach (Match m in Regex.Matches(
            outp,
            @"id\s*=\s*(\d+),.*?health\s*=\s*(\d+).*?pltfmid\s*=\s*Local_([^,\s]+).*?ip\s*=\s*127\.",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id) || id <= 0) continue;
            if (!int.TryParse(m.Groups[2].Value, out int hp) || hp <= 0) continue;
            live.Add((id, m.Groups[3].Value));
        }
        if (live.Count == 0)
        {
            // Loose fallback
            foreach (Match m in Regex.Matches(outp, @"id\s*=\s*(\d+).*?Local_(REFake\d+).*?ip=127", RegexOptions.IgnoreCase | RegexOptions.Singleline))
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

        string[] types = entityName.Contains(',')
            ? entityName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { entityName, "zombieBoe", "zombieSteve", "zombieArlene" };
        foreach (int id in ids.Distinct())
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
        foreach (Match m in Regex.Matches(
            outp, @"id\s*=\s*(\d+),.*?health\s*=\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
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
        byte[] data = Encoding.ASCII.GetBytes(s + "\n");
        _stream.Write(data, 0, data.Length);
        _stream.Flush();
    }

    string ReadAvailable(int waitMs)
    {
        if (_stream == null) return "";
        var end = DateTime.UtcNow.AddMilliseconds(waitMs);
        var tmp = new byte[4096];
        while (DateTime.UtcNow < end)
        {
            try
            {
                if (_stream.DataAvailable)
                {
                    int n = _stream.Read(tmp, 0, tmp.Length);
                    if (n > 0) _buf.Append(Encoding.UTF8.GetString(tmp, 0, n));
                }
                else
                    Thread.Sleep(50);
            }
            catch
            {
                break;
            }
        }
        string all = _buf.ToString();
        if (_buf.Length > 8000)
            _buf.Remove(0, _buf.Length - 4000);
        return all;
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Close(); } catch { /* ignore */ }
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
        _buf.Clear();
    }
}
