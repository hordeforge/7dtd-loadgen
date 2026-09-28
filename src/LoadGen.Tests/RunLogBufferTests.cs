using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// RunLogBuffer backs the --log artifact of a single-bot run. The log delegate
/// it serves is called from the bot's own loop AND from the telnet
/// provisioner's worker thread (each life hands it the same delegate through
/// OnLifeStarted), and the artifact writer enumerates it while that worker may
/// still be running. Pinned here: concurrent writers lose nothing, and a
/// snapshot taken while a writer is active is a stable array rather than a
/// collection that throws mid-iteration.
/// </summary>
public sealed class RunLogBufferTests
{
    [Fact]
    public void Snapshot_PreservesInsertionOrder()
    {
        var buf = new RunLogBuffer();
        buf.Add("STAGE UdpOpen");
        buf.Add("ACTION start");
        Assert.Equal(new[] { "STAGE UdpOpen", "ACTION start" }, buf.Snapshot());
    }

    [Fact]
    public void ConcurrentWriters_LoseNoLines()
    {
        var buf = new RunLogBuffer();
        const int writers = 8, perWriter = 500;

        Parallel.For(0, writers, w =>
        {
            for (int i = 0; i < perWriter; i++)
                buf.Add($"w={w} i={i}");
        });

        var lines = buf.Snapshot();
        Assert.Equal(writers * perWriter, lines.Length);
        // Every line stays whole: a torn append would leave a half-written or
        // duplicated entry, which the --log artifact would carry as evidence.
        Assert.Equal(lines.Length, lines.Distinct().Count());
    }

    [Fact]
    public async Task Snapshot_IsStableWhileAnotherThreadAppends()
    {
        // The artifact writer iterates the snapshot while the provisioner is
        // still appending. On an unsynchronized list that foreach throws
        // "collection was modified" and the run's --log evidence is lost to a
        // stderr fault; here the snapshot is an independent array.
        var buf = new RunLogBuffer();
        using var cts = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested)
                buf.Add($"late {i++}");
        });

        try
        {
            for (int round = 0; round < 200; round++)
            {
                string[] snapshot = buf.Snapshot();
                foreach (string line in snapshot)
                    Assert.False(string.IsNullOrEmpty(line));
            }
        }
        finally
        {
            cts.Cancel();
            await writer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
