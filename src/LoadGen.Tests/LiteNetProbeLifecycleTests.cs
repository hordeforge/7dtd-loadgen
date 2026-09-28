using System.Net;
using System.Net.Sockets;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// One probe opens a NetManager (and its UDP socket) per call, and the probe
/// lane runs up to a thousand of them in one process from LoadRunner. Every
/// exit path has to stop its manager, including the failed-Start branch and a
/// throw out of Start, or the cohort ends holding one live socket per probe.
/// The check is the process's own fd table: a probe that never released its
/// socket shows up as growth no matter which branch it took.
/// </summary>
public sealed class LiteNetProbeLifecycleTests
{
    static int OpenFdCount()
    {
        using var fds = Directory.EnumerateFileSystemEntries("/proc/self/fd").GetEnumerator();
        int n = 0;
        while (fds.MoveNext()) n++;
        return n;
    }

    [Fact]
    public void RepeatedProbes_LeaveNoSocketBehind()
    {
        if (!OperatingSystem.IsLinux()) return; // /proc/self/fd is the probe here

        // A port nothing listens on: the probe opens its socket, connects, and
        // runs out its budget, which is the path a whole failed cohort takes.
        int deadPort;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            deadPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        // Warm the runtime's own lazy allocations (thread pool, JIT, socket
        // internals) so the baseline is not counting first-use growth.
        LiteNetProbe.Run("127.0.0.1", deadPort, "", 300, 1, null, keepLines: false);
        int before = OpenFdCount();
        for (int i = 0; i < 10; i++)
            LiteNetProbe.Run("127.0.0.1", deadPort, "", 300, i + 2, null, keepLines: false);

        Assert.True(OpenFdCount() <= before,
            $"probe runs leaked file descriptors: {before} -> {OpenFdCount()}");
    }
}
