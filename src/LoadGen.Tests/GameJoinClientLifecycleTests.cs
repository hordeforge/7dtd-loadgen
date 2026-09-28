using System.Net;
using System.Net.Sockets;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// A client's NetManager goes into the process-global ActiveNets before
/// LiteNetLib starts it, and every exit path must take it back out. A manager
/// left behind is not inert: the shutdown sweep keeps enumerating it and
/// driving DisconnectAll/Stop for the rest of the process, and the teardown
/// gate is what keeps those calls off a manager another thread is binding.
/// </summary>
/// <remarks>
/// Shares the non-parallel collection because the registry is process-global.
/// </remarks>
[Collection("process-shutdown-sweep")]
public sealed class GameJoinClientLifecycleTests
{
    [Fact]
    public void FailedJoin_ReleasesItsRegisteredManager()
    {
        int before = GameJoinClient.ActiveNets.Count;

        // A port nothing listens on. UDP Connect to a closed port succeeds, so
        // the run gets past the preflight and past NetManager.Start and fails
        // the way a real cohort member fails: a join that never completes.
        int deadPort;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            deadPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        var client = new GameJoinClient();
        int rc = client.Run(new GameJoinClient.Options
        {
            Host = "127.0.0.1",
            Port = deadPort,
            TimeoutMs = 2_000,
            LocalBindIp = "127.0.1.5",
            Log = _ => { },
        });

        Assert.Equal(1, rc);
        Assert.Equal(JoinStage.Failed, client.State.Stage);
        Assert.Equal(before, GameJoinClient.ActiveNets.Count);
    }
}
