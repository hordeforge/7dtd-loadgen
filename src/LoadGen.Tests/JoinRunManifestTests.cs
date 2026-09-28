using System.Text.Json;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// WriteJoinManifest is the --run-manifest writer for join cohorts. Contract
/// pinned here: 7dtd.loadgen.run.v1 identity, the cohort payload passed
/// straight through, one client row per bot ordered by id, and an explicit
/// null scenarioId when the run declared none.
/// </summary>
public sealed class JoinRunManifestTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "loadgen-manifest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    static JoinStateMachine JoinedBot()
    {
        var sm = new JoinStateMachine();
        sm.MarkJoined();
        return sm;
    }

    [Fact]
    public void Writes_RunV1_OneRowPerBot_OrderedById()
    {
        string path = Path.Combine(_dir, "run_manifest.json");
        var cohort = new Dictionary<string, object?> { ["schema"] = "7dtd.loadgen.stats.v1", ["count"] = 2 };

        Program.WriteJoinManifest(path, "re-h500-join-wander", cohort, new[]
        {
            (2, 0, JoinedBot()),
            (1, 0, JoinedBot()),
        });

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        Assert.Equal("7dtd.loadgen.run.v1", root.GetProperty("schema").GetString());
        Assert.Equal("join", root.GetProperty("kind").GetString());
        Assert.Equal("re-h500-join-wander", root.GetProperty("scenarioId").GetString());
        Assert.Equal(2, root.GetProperty("cohort").GetProperty("count").GetInt32());

        JsonElement clients = root.GetProperty("clients");
        Assert.Equal(2, clients.GetArrayLength());
        Assert.Equal(1, clients[0].GetProperty("id").GetInt32());
        Assert.Equal(2, clients[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public void ScenarioId_IsNull_WhenRunDeclaredNone()
    {
        string path = Path.Combine(_dir, "run_manifest.json");

        Program.WriteJoinManifest(path, "", new Dictionary<string, object?>(), Array.Empty<(int, int, JoinStateMachine)>());

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("scenarioId").ValueKind);
    }
}
