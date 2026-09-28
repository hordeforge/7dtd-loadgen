using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>Linux fd accounting, which is the only place the leak is visible
/// (see scripts/procs.py, which reads /proc for the same reason). Reports as
/// skipped rather than passing silently, so a green run never implies the
/// check ran.</summary>
public sealed class LinuxProcFactAttribute : FactAttribute
{
    public LinuxProcFactAttribute()
    {
        if (!Directory.Exists("/proc/self/fd"))
            Skip = "/proc/self/fd is unavailable on this platform";
    }
}

/// <summary>
/// Connect runs once per pressure wave, and the per-life dynamite grant used to
/// add a connection per bot life until TelnetProvisioner gave the cohort one
/// shared console. Anything Connect acquires and fails to release accumulates
/// for the whole run. Dispose releases the socket, so this pins the acquisitions
/// on the connect path itself.
/// </summary>
/// <remarks>
/// What this does and does not prove: it counts open file descriptors, so it
/// catches a TcpClient that survives Connect. It cannot see a wait handle, which
/// on this runtime is managed and holds no fd, so it is not evidence about the
/// former BeginConnect wait-handle regression beyond that regression's socket.
/// The assertions stay on the fd count because that is the quantity a leak of
/// the socket would move.
/// </remarks>
public sealed class TelnetAdminConnectHandleTests
{
    const int WarmupAttempts = 5;
    const int MeasuredAttempts = 20;
    // One fd per leaked connect handle would make every round grow 20.
    const int MeasuredRounds = 3;
    // Margin for the siblings that xunit runs in parallel with this class,
    // which open their own sockets and files while the attempts run; the
    // round minimum, not the worst round, is what is asserted.
    const int AllowedFdGrowth = 8;

    [LinuxProcFact]
    public void RepeatedConnects_DoNotAccumulateFileDescriptors()
    {
        string fdDir = "/proc/self/fd";

        // Port 1 refuses instantly, so the attempts cost no wall clock and the
        // connect wait is still taken on the failing path.
        ConnectAttempts(WarmupAttempts);

        // fd count is process-wide, and xunit runs sibling test classes in
        // parallel inside this process, so a single window also charges their
        // sockets to these connects. Three rounds and the smallest growth: a
        // leaked handle per connect grows every round, a parallel test's
        // socket lands in one of them.
        var growth = new int[MeasuredRounds];
        for (int round = 0; round < MeasuredRounds; round++)
        {
            int before = OpenFileDescriptors(fdDir);
            ConnectAttempts(MeasuredAttempts);
            growth[round] = OpenFileDescriptors(fdDir) - before;
        }

        Assert.True(growth.Min() <= AllowedFdGrowth,
            $"fd count grew by [{string.Join(",", growth)}] over "
            + $"{MeasuredAttempts} telnet connects in {MeasuredRounds} rounds");
    }

    static void ConnectAttempts(int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            using var admin = new TelnetAdmin("127.0.0.1", 1, password: "", log: null);
            Assert.False(admin.Connect(timeoutMs: 2_000), "port 1 must refuse the connect");
        }
        // Release anything the run-time finalizers still hold before counting.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    static int OpenFileDescriptors(string fdDir) =>
        Directory.GetFileSystemEntries(fdDir).Length;
}
