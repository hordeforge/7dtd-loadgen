using System.IO;
using System.Text;
using System.Text.Json;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

public sealed class NetworkStateObserverTests
{
    [Fact]
    public void Joined_IsStructuredAndDeduplicated()
    {
        var events = new List<string>();
        var observer = new NetworkStateObserver(9, new[] { "x" }, Array.Empty<string>(), events.Add);
        observer.Joined(171);
        observer.Joined(171);
        Assert.Single(events);
        Assert.Contains("\"type\":\"joined\"", events[0]);
        Assert.Contains("\"botId\":9", events[0]);
        Assert.Contains("\"entityId\":171", events[0]);
    }

    [Fact]
    public void EveryEvent_CarriesAParsableUtcWallClock()
    {
        // The event file is read next to the console transcript and the
        // server-side capture; elapsedMs alone cannot place an event on either
        // clock, so each line must carry a round-trip UTC stamp.
        var events = new List<string>();
        var observer = new NetworkStateObserver(
            4, new[] { "atomicProtection" }, new[] { "buffAtomicProtected" }, events.Add);

        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        observer.Joined(171);
        observer.Observe("NetPackageModifyCVar", CVar(171, "atomicProtection", 0.5f, 0));
        observer.Observe("NetPackageAddRemoveBuff", Buff(171, "buffAtomicProtected", true));

        Assert.NotEmpty(events);
        foreach (string line in events)
        {
            using var doc = JsonDocument.Parse(line);
            JsonElement t = doc.RootElement.GetProperty("t");
            DateTime stamped = DateTime.Parse(
                t.GetString()!, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.InRange(stamped, before, DateTime.UtcNow.AddSeconds(1));
        }
    }

    [Fact]
    public void SameClocks_SamePackages_ProduceAByteIdenticalEventFile()
    {
        // The event file is the replay artifact a diverged run is diffed
        // against, so two observers fed the same package sequence under the
        // same clock must emit the same bytes: the utc stamp and the elapsed
        // value are the only two fields a wall clock reaches.
        static List<string> Replay()
        {
            var events = new List<string>();
            long now = 0;
            var observer = new NetworkStateObserver(
                3, new[] { "atomicProtection" }, new[] { "buffAtomicProtected" }, events.Add,
                utcNow: () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                elapsedMs: () => now += 10);
            observer.Joined(171);
            observer.Observe("NetPackageModifyCVar", CVar(171, "atomicProtection", 0.5f, 0));
            observer.Observe("NetPackageModifyCVar", CVar(171, "atomicProtection", 0.25f, 2));
            observer.Observe("NetPackageAddRemoveBuff", Buff(171, "buffAtomicProtected", true));
            return events;
        }

        Assert.Equal(Replay(), Replay());
    }

    [Fact]
    public void Joined_EmitsExplicitStateForInactiveWatchedBuff()
    {
        var events = new List<string>();
        var observer = new NetworkStateObserver(
            9, Array.Empty<string>(), new[] { "buffAtomicProtected" }, events.Add);
        observer.Joined(171);
        Assert.Equal(2, events.Count);
        Assert.Contains("\"name\":\"buffAtomicProtected\"", events[1]);
        Assert.Contains("\"active\":false", events[1]);
        Assert.Contains("\"source\":\"joined-default\"", events[1]);
    }

    [Fact]
    public void CVarDelta_AppliesOperations_AndFiltersExactNames()
    {
        var events = new List<string>();
        var observer = new NetworkStateObserver(7, new[] { "atomicProtection" }, Array.Empty<string>(), events.Add);

        observer.Observe("NetPackageModifyCVar", CVar(171, "ignored", 9f, 0));
        observer.Observe("NetPackageModifyCVar", CVar(171, "atomicProtection", 0.5f, 0));
        observer.Observe("NetPackageModifyCVar", CVar(171, "atomicProtection", 0.25f, 2));

        Assert.Equal(2, events.Count);
        using var doc = JsonDocument.Parse(events[^1]);
        Assert.Equal("state", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(171, doc.RootElement.GetProperty("entityId").GetInt32());
        Assert.Equal(0.75f, doc.RootElement.GetProperty("value").GetSingle());
    }

    [Fact]
    public void BuffDelta_MaintainsActiveState()
    {
        var events = new List<string>();
        var observer = new NetworkStateObserver(2, Array.Empty<string>(), new[] { "buffAtomicFallout" }, events.Add);
        observer.Observe("NetPackageAddRemoveBuff", Buff(44, "buffAtomicFallout", true));
        observer.Observe("NetPackageAddRemoveBuff", Buff(44, "buffAtomicFallout", false));
        Assert.Contains("\"active\":true", events[0]);
        Assert.Contains("\"active\":false", events[1]);
    }

    [Fact]
    public void FullState_EmitsFilteredCVarsAndBuffs()
    {
        var events = new List<string>();
        var observer = new NetworkStateObserver(3, new[] { "atomicProtection" }, new[] { "buffAtomicProtected" }, events.Add);
        observer.Observe("NetPackageEntityStatsBuff", Snapshot());
        Assert.Equal(2, events.Count);
        Assert.Contains("\"value\":1", events[0]);
        Assert.Contains("\"active\":true", events[1]);
        Assert.All(events, e => Assert.Contains("\"source\":\"snapshot\"", e));
    }

    [Fact]
    public void FullState_DoesNotInventZeroForAnAbsentCVar()
    {
        var events = new List<string>();
        var observer = new NetworkStateObserver(3, new[] { "notInSnapshot" }, Array.Empty<string>(), events.Add);
        observer.Observe("NetPackageEntityStatsBuff", Snapshot());
        Assert.Empty(events);
    }

    [Fact]
    public void NonFiniteCVarValue_EmitsNull_AndKeepsSinkLive()
    {
        // JSON cannot represent NaN/Infinity; serializing one used to latch
        // SinkFaulted and silently end all evidence capture for the run. A
        // server-side non-finite value must emit as null instead.
        var events = new List<string>();
        var observer = new NetworkStateObserver(6, new[] { "atomicProtection" }, Array.Empty<string>(), events.Add);

        observer.Observe("NetPackageModifyCVar", CVar(171, "atomicProtection", float.NaN, 0));
        observer.Observe("NetPackageModifyCVar", CVar(171, "atomicProtection", float.PositiveInfinity, 2));
        observer.Observe("NetPackageModifyCVar", CVar(171, "atomicProtection", 0.5f, 0));

        Assert.False(observer.SinkFaulted);
        Assert.Equal(3, events.Count);
        Assert.Contains("\"value\":null", events[0]);
        Assert.Contains("\"value\":null", events[1]);
        using var doc = JsonDocument.Parse(events[2]);
        Assert.Equal(0.5f, doc.RootElement.GetProperty("value").GetSingle());
    }

    [Fact]
    public void SinkFault_LatchesAndStopsEmitting()
    {
        // A dead events sink (disk full, deleted file) must not throw out of
        // Joined/Observe - that would kill a healthy joined session - and must
        // stop calling the sink after the first failure.
        int calls = 0;
        void Emit(string _) { calls++; throw new IOException("disk full"); }
        var observer = new NetworkStateObserver(
            5, new[] { "atomicProtection" }, Array.Empty<string>(), Emit);

        observer.Joined(171);
        observer.Observe("NetPackageModifyCVar", CVar(171, "atomicProtection", 1f, 0));

        Assert.True(observer.SinkFaulted);
        Assert.Contains("disk full", observer.SinkError);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EntityState_StaysBoundedUnderEntityChurn()
    {
        // Zombie churn hands the observer fresh entity ids forever; past the
        // cap the tables must drop instead of growing for the whole soak.
        var events = new List<string>();
        var observer = new NetworkStateObserver(
            1, new[] { "atomicProtection" }, Array.Empty<string>(), events.Add);
        const int churn = NetworkStateObserver.MaxTrackedEntities + 64;
        for (int id = 1; id <= churn; id++)
            observer.Observe("NetPackageModifyCVar", CVar(id, "atomicProtection", 1f, 0));

        Assert.True(observer.TrackedCvarEntitiesForTests <= churn,
            "tracked entities exceeded the observed count");
        Assert.True(observer.TrackedCvarEntitiesForTests < churn / 2,
            "per-entity tables were never bounded");
        // State stays live after a bound: a fresh observation still emits.
        int before = events.Count;
        observer.Observe("NetPackageModifyCVar", CVar(churn + 1, "atomicProtection", 0.5f, 0));
        Assert.Equal(before + 1, events.Count);
    }

    [Fact]
    public void CVarDelta_RejectsANameLengthTheBodyCannotHold()
    {
        // The cvar name is length-prefixed by an unauthenticated peer.
        // BinaryReader.ReadString trusts that prefix: a five-byte body claims
        // 2 GiB of name and the allocation takes the whole cohort down with an
        // OutOfMemory no catch around Observe can make safe.
        var observer = new NetworkStateObserver(1, new[] { "atomicProtection" }, Array.Empty<string>(), _ => { });
        Assert.Throws<InvalidDataException>(
            () => observer.Observe("NetPackageModifyCVar", CVarWithHugeName(171)));
    }

    [Fact]
    public void BuffDelta_RejectsANameLengthTheBodyCannotHold()
    {
        var observer = new NetworkStateObserver(1, Array.Empty<string>(), new[] { "buffAtomicProtected" }, _ => { });
        Assert.Throws<InvalidDataException>(
            () => observer.Observe("NetPackageAddRemoveBuff", BuffWithHugeName(171)));
    }

    [Fact]
    public void FullState_RejectsACVarNameLengthTheBodyCannotHold()
    {
        var observer = new NetworkStateObserver(1, new[] { "atomicProtection" }, Array.Empty<string>(), _ => { });
        Assert.Throws<InvalidDataException>(
            () => observer.Observe("NetPackageEntityStatsBuff", SnapshotWithHugeCVarName()));
    }

    /// <summary>7-bit length prefix, low group first, continuation bit set on
    /// every group but the last. Matches BinaryWriter.Write(string).</summary>
    static byte[] HugeNamePrefix() => new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };

    static byte[] CVarWithHugeName(int entityId) => Body(w =>
    {
        w.Write(entityId); w.Write(HugeNamePrefix());
    });

    static byte[] BuffWithHugeName(int entityId) => Body(w =>
    {
        w.Write(entityId); w.Write(HugeNamePrefix());
    });

    static byte[] SnapshotWithHugeCVarName()
    {
        byte[] data = Body(w =>
        {
            w.Write((byte)3);
            w.Write((ushort)0);            // no buffs
            w.Write((ushort)1);            // one cvar
            w.Write(HugeNamePrefix());
        });
        return Body(w => { w.Write(55); w.Write(data.Length); w.Write(data); });
    }

    static byte[] CVar(int entityId, string name, float value, short operation) => Body(w =>
    {
        w.Write(entityId); w.Write(name); w.Write(value); w.Write(operation);
    });

    static byte[] Buff(int entityId, string name, bool adding) => Body(w =>
    {
        w.Write(entityId); w.Write(name); w.Write(12f); w.Write(adding); w.Write(-1);
        w.Write(1); w.Write(2); w.Write(3);
    });

    static byte[] Snapshot()
    {
        byte[] data = Body(w =>
        {
            w.Write((byte)3);
            w.Write((ushort)1);
            w.Write("buffAtomicProtected"); w.Write((byte)1); w.Write((uint)120); w.Write(-1);
            w.Write((byte)0); w.Write((ushort)1); w.Write(0); w.Write(0); w.Write(0);
            w.Write((ushort)1); w.Write("atomicProtection"); w.Write(1f);
        });
        return Body(w => { w.Write(55); w.Write(data.Length); w.Write(data); });
    }

    static byte[] Body(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }
}
