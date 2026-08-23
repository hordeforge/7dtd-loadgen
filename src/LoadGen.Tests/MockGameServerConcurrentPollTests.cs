using System.Collections.Concurrent;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// MockGameServer counters are bumped inside LiteNetLib event handlers, which
/// run on whatever thread calls Poll(). Multiple concurrent pollers must be
/// safe: no thrown exceptions, and the full join handshake still completes
/// (challenge sent once, echoed back, verified; login accepted).
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
                WanderUntilDeath = false,
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
        Assert.Equal(1, server.ChallengesSent);
        Assert.Equal(server.ChallengesSent, server.ChallengesOk);
        Assert.Equal(1, server.LoginsAccepted);
    }
}
