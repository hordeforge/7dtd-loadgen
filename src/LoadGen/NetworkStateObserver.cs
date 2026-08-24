using System.Text;
using System.Text.Json;

namespace SevenDTD.LoadGen;

/// <summary>
/// Filtered, client-observed entity CVar and buff state. It decodes only the
/// stock replication packets needed for assertions and stays disabled for
/// ordinary load runs.
/// </summary>
public sealed class NetworkStateObserver
{
    readonly int _botId;
    readonly HashSet<string> _cvarFilters;
    readonly HashSet<string> _buffFilters;
    readonly Action<string> _emit;
    readonly Dictionary<int, Dictionary<string, float>> _cvars = new();
    readonly Dictionary<int, HashSet<string>> _buffs = new();
    readonly HashSet<int> _joinedEntities = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    long _sequence;

    public NetworkStateObserver(
        int botId, IEnumerable<string> cvarFilters, IEnumerable<string> buffFilters,
        Action<string> emit)
    {
        _botId = botId;
        _cvarFilters = new HashSet<string>(cvarFilters, StringComparer.Ordinal);
        _buffFilters = new HashSet<string>(buffFilters, StringComparer.Ordinal);
        _emit = emit;
    }

    public bool Enabled => _cvarFilters.Count > 0 || _buffFilters.Count > 0;

    public void Joined(int entityId)
    {
        if (!_joinedEntities.Add(entityId)) return;
        Emit(new
        {
            schema = "7dtd.loadgen.event.v1",
            type = "joined",
            botId = _botId,
            entityId,
            seq = NextSequence(),
            elapsedMs = _clock.ElapsedMilliseconds,
        });
    }

    public void Observe(string packageType, ReadOnlySpan<byte> body)
    {
        if (!Enabled) return;
        if (packageType == "NetPackageModifyCVar") ObserveCVar(body);
        else if (packageType == "NetPackageAddRemoveBuff") ObserveBuffDelta(body);
        else if (packageType == "NetPackageEntityStatsBuff") ObserveFullState(body);
    }

    void ObserveCVar(ReadOnlySpan<byte> body)
    {
        using var reader = Reader(body);
        int entityId = reader.ReadInt32();
        string name = reader.ReadString();
        float operand = reader.ReadSingle();
        short operation = reader.ReadInt16();
        if (!_cvarFilters.Contains(name)) return;

        var state = CvarsFor(entityId);
        state.TryGetValue(name, out float current);
        float value = Apply(current, operand, operation);
        state[name] = value;
        EmitState("cvar", entityId, name, value, null, "delta");
    }

    void ObserveBuffDelta(ReadOnlySpan<byte> body)
    {
        using var reader = Reader(body);
        int entityId = reader.ReadInt32();
        string name = reader.ReadString();
        _ = reader.ReadSingle();
        bool adding = reader.ReadBoolean();
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        if (!_buffFilters.Contains(name)) return;

        var state = BuffsFor(entityId);
        if (adding) state.Add(name); else state.Remove(name);
        EmitState("buff", entityId, name, null, adding, "delta");
    }

    void ObserveFullState(ReadOnlySpan<byte> body)
    {
        using var reader = Reader(body);
        int entityId = reader.ReadInt32();
        int dataLength = reader.ReadInt32();
        if (dataLength < 0 || dataLength > body.Length - 8)
            throw new InvalidDataException($"EntityStatsBuff data length {dataLength} exceeds body");
        using var stateReader = Reader(reader.ReadBytes(dataLength));
        byte version = stateReader.ReadByte();
        ushort buffCount = stateReader.ReadUInt16();
        var buffs = BuffsFor(entityId);
        buffs.Clear();
        for (int i = 0; i < buffCount; i++)
        {
            string name = ReadBuffValue(stateReader, version);
            if (_buffFilters.Contains(name)) buffs.Add(name);
        }

        ushort cvarCount = stateReader.ReadUInt16();
        var cvars = CvarsFor(entityId);
        var observedSnapshotCvars = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < cvarCount; i++)
        {
            string name = stateReader.ReadString();
            float value = stateReader.ReadSingle();
            if (_cvarFilters.Contains(name))
            {
                cvars[name] = value;
                observedSnapshotCvars.Add(name);
            }
        }

        // EntityBuffs.Read replaces ActiveBuffs but merges the serialized CVar
        // entries into its existing dictionary. Mirror that distinction here:
        // absent CVars are not removals and must not generate false zero state.
        foreach (string name in observedSnapshotCvars)
            EmitState("cvar", entityId, name, cvars[name], null, "snapshot");
        foreach (string name in _buffFilters)
            EmitState("buff", entityId, name, null, buffs.Contains(name), "snapshot");
    }

    static string ReadBuffValue(BinaryReader reader, byte version)
    {
        if (version < 2)
            throw new InvalidDataException($"legacy EntityBuffs version {version} has hashed names and is unsupported");
        string name = reader.ReadString();
        _ = reader.ReadByte();
        _ = reader.ReadUInt32();
        _ = reader.ReadInt32();
        _ = reader.ReadByte();
        if (version == 0) _ = reader.ReadByte(); else _ = reader.ReadUInt16();
        if (version >= 3)
        {
            _ = reader.ReadInt32();
            _ = reader.ReadInt32();
            _ = reader.ReadInt32();
        }
        return name;
    }

    static float Apply(float current, float operand, short operation) => operation switch
    {
        0 or 1 => operand,
        2 => current + operand,
        3 => current - operand,
        4 => current * operand,
        5 => operand == 0f ? current : current / operand,
        6 => current + current * operand,
        7 => current - current * operand,
        _ => throw new InvalidDataException($"unknown CVar operation {operation}"),
    };

    void EmitState(string kind, int entityId, string name, float? value, bool? active, string source) => Emit(new
    {
        schema = "7dtd.loadgen.event.v1",
        type = "state",
        botId = _botId,
        entityId,
        kind,
        name,
        value,
        active,
        source,
        seq = NextSequence(),
        elapsedMs = _clock.ElapsedMilliseconds,
    });

    void Emit(object value) => _emit(JsonSerializer.Serialize(value));
    long NextSequence() => Interlocked.Increment(ref _sequence);
    Dictionary<string, float> CvarsFor(int entityId) =>
        _cvars.TryGetValue(entityId, out var value) ? value : _cvars[entityId] = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> BuffsFor(int entityId) =>
        _buffs.TryGetValue(entityId, out var value) ? value : _buffs[entityId] = new(StringComparer.Ordinal);
    static BinaryReader Reader(ReadOnlySpan<byte> body) =>
        new(new MemoryStream(body.ToArray(), writable: false), Encoding.UTF8, leaveOpen: false);
}

/// <summary>Thread-safe JSON-lines sink shared by an observing bot cohort.</summary>
public sealed class JsonLineEventWriter : IDisposable
{
    readonly StreamWriter _writer;
    readonly object _gate = new();

    public JsonLineEventWriter(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new StreamWriter(new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        { AutoFlush = true };
    }

    public void Write(string json)
    {
        lock (_gate) _writer.WriteLine(json);
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
