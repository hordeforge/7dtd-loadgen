using System.Collections.Concurrent;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// MockGameServer counters are bumped inside LiteNetLib event handlers, which
/// run on whatever thread calls Poll(). Multiple concurrent pollers must be
/// safe: no thrown exceptions, the handshake still completes, and every
/// challenge is counted exactly once (atomic increments, no lost updates).
/// </summary>
public sealed class MockGameServerConcurrentPollTests
{
    [Fact]
    public async Task ConcurrentPollers_HandshakeSucceeds_CountersExact()
    {
        using var server = new MockGameServer();
        server.Start(0);
        var stop = new CancellationTokenSource();
        var errors = new ConcurrentBag<Exception>();
        var pollers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    server.Poll();
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })).ToArray();

        var result = LiteNetProbe.Run("127.0.0.1", server.Port, "", 8000, 1);

        stop.Cancel();
        await Task.WhenAll(pollers).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(errors.IsEmpty, "poller faulted: " + string.Join("; ", errors.Select(e => e.Message)));
        Assert.True(result.Pass, $"probe did not progress: stages=[{string.Join(",", result.Stages)}]");
        Assert.Equal(1, server.ChallengesSent);
        Assert.Equal(server.ChallengesSent, server.ChallengesOk);
    }
}
