using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>
/// Connect runs once per pressure wave, and the per-life dynamite grant used to
/// add a connection per bot life until TelnetProvisioner gave the cohort one
/// shared console. A handle Connect fails to release accumulates for the whole
/// run either way. The socket itself is released by Dispose; this pins the other
/// acquisition on the same path, the connect wait.
/// </summary>
public sealed class TelnetAdminConnectHandleTests
{
    const int WarmupAttempts = 5;
    const int MeasuredAttempts = 20;
    // One fd per leaked connect handle would make the delta 20. The margin
    // covers xunit running sibling test classes in parallel, which open their
    // own sockets and files while the attempts run.
    const int AllowedFdGrowth = 8;

    [Fact]
    public void RepeatedConnects_DoNotAccumulateFileDescriptors()
    {
        // Linux-only accounting; the lab and CI both run there (see
        // scripts/procs.py, which reads /proc for the same reason).
        string fdDir = "/proc/self/fd";
        if (!Directory.Exists(fdDir)) return;

        // Port 1 refuses instantly, so the attempts cost no wall clock and the
        // connect wait is still taken on the failing path.
        ConnectAttempts(WarmupAttempts);
        int before = OpenFileDescriptors(fdDir);

        ConnectAttempts(MeasuredAttempts);

        int after = OpenFileDescriptors(fdDir);
        Assert.True(after - before <= AllowedFdGrowth,
            $"fd count grew {before} -> {after} over {MeasuredAttempts} telnet connects");
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
