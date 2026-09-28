using System.Collections.Concurrent;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// MockGameServer is polled from several threads at once. Those threads must be
/// safe: no thrown exceptions, never two pollers inside one NetManager, and the
/// full join handshake still completes (challenge sent once, echoed back,
/// verified; login accepted).
/// </summary>
[Collection("process-shutdown-sweep")]
public sealed class MockGameServerConcurrentPollTests
{
    const int Pollers = 4;

    private static Task[] StartPollers(MockGameServer server, CancellationTokenSource stop, ConcurrentBag<Exception> errors) =>
        Enumerable.Range(0, Pollers).Select(_ => Task.Run(() =>
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

    [Fact]
    public async Task ConcurrentPollers_HandshakeSucceeds_CountersExact()
    {
        using var server = new MockGameServer();
        server.Start(0);
        var stop = new CancellationTokenSource();
        var errors = new ConcurrentBag<Exception>();
        var pollers = StartPollers(server, stop, errors);

        // LiteNetProbe only counts bytes; it never echoes the challenge, so the
        // handshake contract needs a real client. GameJoinClient performs
        // challenge echo -> PackageIds -> login -> spawn against this server.
        var client = new GameJoinClient();
        int rc;
        try
        {
            rc = client.Run(new GameJoinClient.Options
            {
                Host = "127.0.0.1",
                Port = server.Port,
                PlayerName = "REFake",
                TimeoutMs = 20_000,
                ActionCount = 12,
                Mode = ActionLoop.BotMode.Mixed,
                Death = ActionLoop.DeathMethod.None,
                Respawn = false,
                CohortSize = 1,
                PaceMs = 5,
            });
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(pollers).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(errors.IsEmpty, "poller faulted: " + string.Join("; ", errors.Select(e => e.Message)));
        Assert.True(rc == 0 || client.State.EverJoined,
            $"handshake did not complete: rc={rc} stage={client.State.Stage} fail={client.State.FailReason}");
        Assert.Equal(1, server.MaxConcurrentPolls);
        Assert.Equal(1, server.ChallengesSent);
        Assert.Equal(server.ChallengesSent, server.ChallengesOk);
        Assert.Equal(1, server.LoginsAccepted);
    }

    /// <summary>Poll() must never let two threads into one NetManager.
    /// LiteNetLib's manager is single-threaded by contract: concurrent
    /// PollEvents calls dequeue the shared incoming queues and run the peer
    /// handlers at the same time, which drops or duplicates packages and
    /// corrupts the queues. The peak is recorded inside Poll(), so removing the
    /// gate is exactly what makes this assertion fail.</summary>
    [Fact]
    public async Task Poll_NeverRunsTwoPollersAtOnce()
    {
        using var server = new MockGameServer();
        server.Start(0);
        var stop = new CancellationTokenSource();
        var errors = new ConcurrentBag<Exception>();
        var pollers = StartPollers(server, stop, errors);

        try
        {
            await Task.Delay(500);
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(pollers).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(errors.IsEmpty, "poller faulted: " + string.Join("; ", errors.Select(e => e.Message)));
        Assert.Equal(1, server.MaxConcurrentPolls);
    }
}
