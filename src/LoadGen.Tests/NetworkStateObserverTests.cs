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
