using System.Text;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// JsonLineEventWriter is the --events-jsonl sink shared by an observing bot
/// cohort. Contract pinned here: missing parent directories are created, every
/// Write lands as exactly one line without a UTF-8 BOM (external line parsers
/// consume the file), concurrent writers lose nothing, and Dispose is safe.
/// </summary>
public sealed class JsonLineEventWriterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "loadgen-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    string[] ReadAllLines(string path) => File.ReadAllLines(path, new UTF8Encoding(false));

    [Fact]
    public void Write_CreatesMissingDirectories_OneLinePerWrite_NoBom()
    {
        string path = Path.Combine(_dir, "nested", "events.jsonl");
        using (var sink = new JsonLineEventWriter(path))
        {
            sink.Write("{\"type\":\"joined\"}");
            sink.Write("{\"type\":\"state\"}");
        }

        byte[] raw = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, raw[0]); // no BOM: parsers must see '{' first

        string[] lines = ReadAllLines(path);
        Assert.Equal(new[] { "{\"type\":\"joined\"}", "{\"type\":\"state\"}" }, lines);
    }

    [Fact]
    public void ConcurrentWriters_LoseNoLines()
    {
        string path = Path.Combine(_dir, "conc.jsonl");
        const int tasks = 8, perTask = 250;
        using (var sink = new JsonLineEventWriter(path))
        {
            Parallel.For(0, tasks, t =>
            {
                for (int i = 0; i < perTask; i++)
                    sink.Write($"{{\"t\":{t},\"i\":{i}}}");
            });
        }

        var lines = ReadAllLines(path);
        Assert.Equal(tasks * perTask, lines.Length);
        // Every line stays whole JSON: no torn interleaved writes.
        Assert.All(lines, l => Assert.EndsWith("}", l));
    }
}
