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

    /// <summary>How far past an "id=" anchor one listplayers row may extend.
    /// Console rows are a few hundred characters; nothing real comes close.</summary>
    internal const int MaxRowScanChars = 512;

    // Row parsers below walk the response field by field inside a bounded
    // window instead of running an unbounded `.*?` chain over the whole
    // response. The chain retried the full remaining text at every start
    // position, so a response without the tail fields cost O(n^2): 16 KB of
    // server-controlled text spent 9 s in one SpawnZombiesNearPlayers call,
    // and the response is unauthenticated telnet output. Field scanning keeps
    // the cost linear in the response length.

    /// <summary>Step over `name '='`, whitespace-tolerant, at or after
    /// <paramref name="pos"/>. False when the field is not in the row.</summary>
    static bool TrySkipField(ReadOnlySpan<char> row, ref int pos, string name)
    {
        int at = row.Slice(pos).IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        pos += at + name.Length;
        while (pos < row.Length && char.IsWhiteSpace(row[pos])) pos++;
        if (pos >= row.Length || row[pos] != '=') return false;
        pos++;
        while (pos < row.Length && char.IsWhiteSpace(row[pos])) pos++;
        return true;
    }

    /// <summary>Read a bare unsigned decimal at <paramref name="pos"/>. False
    /// when there is no digit there or the value overflows Int32 (a crafted
    /// row must not wrap into a small, killable id).</summary>
    static bool TryReadUInt(ReadOnlySpan<char> row, int pos, out int value, out int end)
    {
        value = 0;
        end = pos;
        long acc = 0;
        int i = pos;
        while (i < row.Length && row[i] is >= '0' and <= '9')
        {
            acc = acc * 10 + (row[i] - '0');
            if (acc > int.MaxValue) return false;
            i++;
        }
        if (i == pos) return false;
        value = (int)acc;
        end = i;
        return true;
    }

    /// <summary>Read a token of non-separator characters at
    /// <paramref name="pos"/> (listplayers terminates tokens with ',' or
    /// whitespace). False on an empty token.</summary>
    static bool TryReadToken(ReadOnlySpan<char> row, int pos, out string token, out int end)
    {
        token = "";
        end = pos;
        int i = pos;
        while (i < row.Length && row[i] != ',' && !char.IsWhiteSpace(row[i])) i++;
        if (i == pos) return false;
        token = row.Slice(pos, i - pos).ToString();
        end = i;
        return true;
    }

    /// <summary>Advance to the next "id" field anchor at or after
    /// <paramref name="pos"/>.</summary>
    static int FindIdAnchor(ReadOnlySpan<char> outp, int pos)
    {
        while (true)
        {
            int at = outp.Slice(pos).IndexOf("id", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1;
            int i = pos + at + 2;
            while (i < outp.Length && char.IsWhiteSpace(outp[i])) i++;
            if (i < outp.Length && outp[i] == '=') return pos + at;
            pos = pos + at + 2;
        }
    }

    /// <summary>One row's fields, as far as they could be read. A field the
    /// console did not print (or that the server garbled) stays absent rather
    /// than defaulting to a plausible value.</summary>
    internal readonly record struct PlayerRow(
        bool HasId, int Id, bool HasHealth, int Health, bool HasPlatform, string Token, bool LoopbackIp);

    /// <summary>Field-scan a raw listplayers response, one bounded window per
    /// "id=" anchor. <see cref="NextPos"/> is where the next scan starts (past
    /// the last field this row actually read, so rows stay non-overlapping the
    /// way Regex.Matches was); the callers below apply their own row
    /// acceptance rules.</summary>
    internal static IEnumerable<(PlayerRow Row, int NextPos)> ScanPlayerRows(string outp)
    {
        int pos = 0;
        while (pos < outp.Length)
        {
            int anchor = FindIdAnchor(outp, pos);
            if (anchor < 0) yield break;
            int rowEnd = Math.Min(outp.Length, anchor + MaxRowScanChars);
            ReadOnlySpan<char> row = outp.AsSpan(anchor, rowEnd - anchor);

            bool hasId = false, hasHealth = false, hasPlatform = false, loopback = false;
            int id = 0, health = 0, p = 0, consumed = 2;
            string token = "";
            if (TrySkipField(row, ref p, "id") && TryReadUInt(row, p, out id, out int afterId))
            {
                hasId = true;
                consumed = Math.Max(consumed, afterId);
                if (afterId < row.Length && row[afterId] == ','
                    && TrySkipField(row, ref afterId, "health"))
                    hasHealth = TryReadUInt(row, afterId, out health, out int afterHealth);
                consumed = Math.Max(consumed, afterId);
            }
            if (TrySkipField(row, ref p, "pltfmid")
                && row.Slice(p).StartsWith(PlatformPrefix, StringComparison.OrdinalIgnoreCase)
                && TryReadToken(row, p + PlatformPrefix.Length, out string t, out int afterToken))
            {
                hasPlatform = true;
                token = t;
                consumed = Math.Max(consumed, afterToken);
            }
            if (TrySkipField(row, ref p, "ip"))
            {
                loopback = row.Slice(p).StartsWith(LoopbackPrefix, StringComparison.OrdinalIgnoreCase);
                consumed = Math.Max(consumed, p);
            }
            consumed = Math.Min(consumed, row.Length);

            yield return (new PlayerRow(hasId, id, hasHealth, health, hasPlatform, token, loopback),
                          anchor + consumed);
            pos = anchor + consumed;
        }
    }

    const string PlatformPrefix = "Local_";
    const string FallbackTokenPrefix = "REFake";
    const string LoopbackPrefix = "127.";

    /// <summary>Living connected players from a raw listplayers response: id,
    /// health, platform id and the loopback ip the console prints. Only rows
    /// whose token passes <see cref="IsSafeCommandToken"/> are returned: the
    /// token is server-controlled text that later becomes an admin command
    /// argument, so an unsafe one is dropped, never sanitized.</summary>
    internal static List<(int id, string token)> ParseLivePlayerRows(string outp, Action<string>? log = null)
    {
        var rows = new List<(int, string)>();
        foreach (var (r, _) in ScanPlayerRows(outp))
        {
            if (!r.HasId || r.Id <= 0 || !r.HasHealth || r.Health <= 0
                || !r.HasPlatform || !r.LoopbackIp)
                continue;
            if (IsSafeCommandToken(r.Token))
                rows.Add((r.Id, r.Token));
            else
                log?.Invoke($"TELNET skipped unsafe player token (len={r.Token.Length})");
        }
        return rows;
    }

    /// <summary>Loose fallback for consoles that print the platform id and the
    /// loopback ip but not health: id + "Local_REFakeN".</summary>
    internal static List<(int id, string token)> ParseFallbackPlayerRows(string outp)
    {
        var rows = new List<(int, string)>();
        foreach (var (r, _) in ScanPlayerRows(outp))
        {
            if (r.HasId && r.Id > 0 && r.HasPlatform && r.LoopbackIp
                && r.Token.StartsWith(FallbackTokenPrefix, StringComparison.OrdinalIgnoreCase))
                rows.Add((r.Id, r.Token));
        }
        return rows;
    }

    /// <summary>ids of living players from a raw listplayers response, for the
    /// horde wave picker.</summary>
    internal static List<int> ParseLivingPlayerIds(string outp)
    {
        var ids = new List<int>();
        foreach (var (r, _) in ScanPlayerRows(outp))
        {
            if (r.HasId && r.Id > 0 && r.HasHealth && r.Health > 0)
                ids.Add(r.Id);
        }
        return ids;
    }

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
            // ConnectAsync plus a bounded wait, not BeginConnect: IAsyncResult.
            // AsyncWaitHandle materializes a kernel-backed ManualResetEvent that
            // only disposing the result releases, and nothing here ever disposed
            // it, so every pressure wave and every per-bot dynamite give leaked a
            // handle for the life of the run. A failed connect surfaces from Wait
            // as AggregateException, which the catch below already logs and tears
            // down.
            var connect = _tcp.ConnectAsync(_host, _port);
            if (!connect.Wait(timeoutMs))
            {
                _log?.Invoke($"TELNET connect timeout {_host}:{_port}");
                // Disposing the socket below faults the still-pending connect;
                // observe that fault so the abandoned task cannot resurface as
                // an unobserved task exception later in the run.
                _ = connect.ContinueWith(static t => t.Exception, TaskScheduler.Default);
                Dispose();
                return false;
            }
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

    static readonly string[] DefaultSpawnEntityTypes =
        { "zombieBoe", "zombieSteve", "zombieArlene" };

    /// <summary>Comma list from --spawn-entity to concrete entity classes. A
    /// comma-only list (" , ") splits to zero entries under RemoveEmptyEntries,
    /// and the spawn loop then divides by zero on types.Length every wave; an
    /// empty selection means the default mix, same as an unset value. Entries
    /// that could carry extra arguments into `spawnentity <id> <type>` are
    /// dropped: only the allowlisted token shape reaches the console.</summary>
    internal static string[] ResolveSpawnEntityTypes(string? entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName)) return DefaultSpawnEntityTypes;
        string[] parts = entityName.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] safe = Array.FindAll(parts, IsSafeCommandToken);
        return safe.Length > 0 ? safe : DefaultSpawnEntityTypes;
    }

    /// <summary>
    /// Apply world pressure: try zombie se first; optional kill fallback for worlds
    /// without AI spawn points (empty height-test maps).
    /// </summary>
    public int SpawnZombiesNearPlayers(string entityName = "zombieBoe", int perPlayer = 3)
    {
        string outp = Exec("listplayers");
        // Prefer living connected bots only (skip health=0 leftovers from prior kills);
        // unsafe server-supplied tokens are dropped inside the parser.
        var live = ParseLivePlayerRows(outp, _log);
        if (live.Count == 0)
        {
            // Loose fallback
            live = ParseFallbackPlayerRows(outp);
        }
        var ids = live.Select(x => x.id).Distinct().ToList();
        var names = live.Select(x => x.token).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
        string[] types = ResolveSpawnEntityTypes(entityName);
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
        var ids = ParseLivingPlayerIds(outp);
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

    // The console answers a command with one short burst and then goes quiet;
    // waitMs is an upper bound, not the cost of a command. Ending the window
    // on ReadQuietGapMs of silence (well under the burst spacing of any real
    // reply) turns a wave of N commands from N*waitMs into N round trips,
    // which is the whole cost model of spawn pressure at cohort scale. The
    // shortcut needs a burst to measure: a window that has seen nothing at all
    // still waits out waitMs, so a slow console's banner and password prompt
    // are never cut short.
    internal const int ReadQuietGapMs = 100;
    // Read-poll interval; fine enough that the quiet gap is measured, not the
    // poll, and coarse enough to leave the loopbox to the server.
    const int ReadPollMs = 10;

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
        long lastDataMs = 0;
        bool sawData = false;
        while (sw.ElapsedMilliseconds < waitMs)
        {
            try
            {
                if (_stream.DataAvailable)
                {
                    int n = _stream.Read(tmp, 0, tmp.Length);
                    if (n > 0)
                    {
                        _buf.Append(_decoder.Decode(tmp.AsSpan(0, n)));
                        lastDataMs = sw.ElapsedMilliseconds;
                        sawData = true;
                    }
                }
                else if (sawData && sw.ElapsedMilliseconds - lastDataMs >= ReadQuietGapMs)
                {
                    // The console stopped talking. Output that arrives after
                    // the window closes stays in _buf and is returned by the
                    // next Exec, exactly as when a reply overran waitMs.
                    break;
                }
                else
                    Thread.Sleep(ReadPollMs);
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
        // Closing an already-reset socket throws IOException/SocketException.
        // Dispose runs from `using` on both the success and failure paths, so a
        // throw here would replace the caller's result with a teardown error.
        try { _stream?.Dispose(); } catch (Exception) { }
        try { _tcp?.Close(); } catch (Exception) { }
        try { _tcp?.Dispose(); } catch (Exception) { }
        _stream = null;
        _tcp = null;
        _buf.Clear();
        _decoder.Reset();
    }
}
