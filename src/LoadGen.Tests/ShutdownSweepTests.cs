using System.Diagnostics;
using LiteNetLib;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// DisconnectAllActive is the process-wide Ctrl+C/ProcessExit teardown. It must
/// flip ShutdownRequested so live client loops unwind before any manager is
/// touched, actually stop registered managers, and run exactly once per process
/// (a second call is a cheap no-op instead of a second concurrent sweep).
/// </summary>
/// <remarks>
/// The sweep is process-global state: while it runs, any live GameJoinClient
/// sees ShutdownRequested and unwinds. The shared non-parallel collection keeps
/// this test from overlapping the one other test that drives a real client, and
/// the finally block resets the one-shot so test order never matters.
/// </remarks>
[Collection("process-shutdown-sweep")]
public sealed class ShutdownSweepTests
{
    [Fact]
    public void Sweep_FlagsShutdown_StopsRegisteredManager_SecondCallIsNoOp()
    {
        Assert.False(GameJoinClient.ShutdownRequested);

        var listener = new EventBasedNetListener();
        var net = new NetManager(listener) { AutoRecycle = true };
        Assert.True(net.Start());
        GameJoinClient.ActiveNets[net] = 0;
        try
        {
            var sw = Stopwatch.StartNew();
            GameJoinClient.DisconnectAllActive();
            sw.Stop();

            Assert.True(GameJoinClient.ShutdownRequested);
            Assert.False(net.IsRunning);
            // 300ms unwind grace + 200ms BYE drain bound the sweep from below;
            // a faster return means a sleep was dropped and bots would race it.
            Assert.True(sw.ElapsedMilliseconds >= 450,
                $"sweep returned too early ({sw.ElapsedMilliseconds}ms); grace/drain sleeps missing");

            // One-shot guard: the second pass must not re-drive managers.
            var sw2 = Stopwatch.StartNew();
            GameJoinClient.DisconnectAllActive();
            sw2.Stop();
            Assert.True(sw2.ElapsedMilliseconds < 100,
                $"second sweep took {sw2.ElapsedMilliseconds}ms; one-shot guard broken");
        }
        finally
        {
            GameJoinClient.ActiveNets.TryRemove(net, out _);
            GameJoinClient.ResetShutdownForTests();
        }
    }
}

/// <summary>Marker so the sweep and any live-client test never overlap.</summary>
[CollectionDefinition("process-shutdown-sweep", DisableParallelization = true)]
public sealed class ProcessShutdownSweepCollection { }
