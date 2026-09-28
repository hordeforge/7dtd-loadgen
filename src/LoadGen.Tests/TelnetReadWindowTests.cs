using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>Loopback stand-in for the dedicated's telnet console: one
/// connection per client, newline-framed commands, scripted replies.</summary>
internal sealed class FakeTelnetConsole : IDisposable
{
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentQueue<string> _received = new();
    int _connections;

    public int Port { get; }
    public IReadOnlyCollection<string> Received => _received;
    public int Connections => Volatile.Read(ref _connections);

    /// <param name="respond">Reply for one command line, or null to stay silent.</param>
    /// <param name="stream">If set, this text is written in chunks for the
    /// first N ms of every command, so the read window never sees a quiet gap.</param>
    public FakeTelnetConsole(
        Func<string, string?>? respond = null,
        (string text, int chunkEveryMs, int totalMs)? stream = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoop(respond, stream));
    }

    async Task AcceptLoop(
        Func<string, string?>? respond,
        (string text, int chunkEveryMs, int totalMs)? stream)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => Serve(client, respond, stream));
        }
    }

    async Task Serve(
        TcpClient client,
        Func<string, string?>? respond,
        (string text, int chunkEveryMs, int totalMs)? stream)
    {
        using (client)
        {
            client.NoDelay = true;
            var streamOut = client.GetStream();
            var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!_cts.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(_cts.Token); }
                catch { return; }
                if (line == null) return;
                _received.Enqueue(line);
                string? reply = respond?.Invoke(line);
                if (reply != null)
                {
                    try
                    {
                        await streamOut.WriteAsync(Encoding.UTF8.GetBytes(reply), _cts.Token);
                        await streamOut.FlushAsync(_cts.Token);
                    }
                    catch { return; }
                }
                if (stream is { } s)
                {
                    // Keep writing past the client's read window so the window
                    // must still terminate on its own upper bound.
                    while (sw.ElapsedMilliseconds < s.totalMs)
                    {
                        try
                        {
                            await streamOut.WriteAsync(Encoding.UTF8.GetBytes(s.text), _cts.Token);
                            await streamOut.FlushAsync(_cts.Token);
                        }
                        catch { return; }
                        await Task.Delay(s.chunkEveryMs, _cts.Token);
                    }
                }
            }
        }
    }

    public bool WaitForCommands(int count, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_received.Count >= count) return true;
            Thread.Sleep(10);
        }
        return _received.Count >= count;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}

public sealed class TelnetReadWindowTests
{
    [Fact]
    public void Exec_ReturnsWhenTheConsoleGoesQuiet()
    {
        // The read window is an upper bound, not the cost of a command. A
        // console that answers instantly must not cost the operator the full
        // window per command, or a spawn wave costs thousands of commands times
        // the window.
        using var console = new FakeTelnetConsole(respond: cmd => $"ok: {cmd}");
        using var admin = new TelnetAdmin("127.0.0.1", console.Port, password: "", log: null);
        Assert.True(admin.Connect());
        console.WaitForCommands(1, 5_000); // drain the banner-window read

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int Commands = 5;
        for (int i = 0; i < Commands; i++)
        {
            string response = admin.Exec($"give {i} thrownDynamite 3");
            Assert.Contains($"give {i} thrownDynamite 3", response);
        }
        sw.Stop();

        Assert.True(console.WaitForCommands(Commands, 5_000));
        Assert.True(sw.ElapsedMilliseconds < Commands * 350,
            $"{Commands} answered commands took {sw.ElapsedMilliseconds}ms: the read window is not closing on quiescence");
    }

    [Fact]
    public void Exec_StaysBoundedWhenTheConsoleNeverGoesQuiet()
    {
        // Continuous output must not turn the read window into an open-ended
        // read: the window still ends at its upper bound, and the bytes stay
        // buffered for the next command.
        using var console = new FakeTelnetConsole(
            respond: _ => null,
            stream: ("X", chunkEveryMs: 20, totalMs: 1_500));
        using var admin = new TelnetAdmin("127.0.0.1", console.Port, password: "", log: null);
        Assert.True(admin.Connect());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        admin.Exec("listplayers");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2_000,
            $"Exec over a continuous stream took {sw.ElapsedMilliseconds}ms: the read window lost its bound");
    }

    [Fact]
    public void Exec_OverSilentConsoleStillWaitsOutItsWindow()
    {
        // Quiescence is only a shortcut once the console has spoken. A window
        // that has seen nothing keeps its full bound, so a slow banner or a
        // command the console does not echo is never cut short.
        using var console = new FakeTelnetConsole(respond: _ => null);
        using var admin = new TelnetAdmin("127.0.0.1", console.Port, password: "", log: null);
        Assert.True(admin.Connect());
        console.WaitForCommands(1, 5_000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("", admin.Exec("givespawn"));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 600, $"silent command took {sw.ElapsedMilliseconds}ms");
    }
}

public sealed class TelnetProvisionerTests
{
    [Fact]
    public void CohortSharesOneConsoleConnection()
    {
        // Per-life grants are cohort control-plane traffic, not per-bot work:
        // one connection, serial commands, and the bot side never waits on the
        // console. A connection per life is the failure this pins.
        using var console = new FakeTelnetConsole(respond: cmd => $"Gave 3: {cmd}");
        var provisioner = new TelnetProvisioner(
            () => new TelnetAdmin("127.0.0.1", console.Port, password: "", log: null),
            _ => { });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int Grants = 40;
        for (int i = 0; i < Grants; i++)
            provisioner.Enqueue($"give {i} thrownDynamite 3", i, null);
        sw.Stop();
        int receivedBeforeDrain = console.Received.Count;

        Assert.True(console.WaitForCommands(Grants, 30_000),
            $"only {console.Received.Count}/{Grants} grants reached the console");
        provisioner.Dispose();
        Assert.Equal(0, provisioner.Dropped);

        // Enqueueing is the bot's cost and must not scale with the console. The
        // proof is structural, not a wall-clock budget: a loop that waits on a
        // round trip per grant has necessarily delivered all of them by the
        // time it returns, while queue appends outrun the console by orders of
        // magnitude. A millisecond bound tight enough to catch the blocking
        // shape also fails on a loaded CI runner through preemption alone, and
        // a gate that red for the wrong reason stops being read.
        Assert.True(receivedBeforeDrain < Grants,
            $"all {Grants} grants reached the console before the enqueue loop returned "
            + $"({sw.ElapsedMilliseconds}ms): the bot loop is blocking on the console");

        int give = console.Received.Count(c => c.StartsWith("give ", StringComparison.Ordinal));
        Assert.Equal(Grants, give);
        Assert.Equal(1, console.Connections);
    }

    [Fact]
    public void Enqueue_DropsOldestAndCountsWhenTheCohortOutrunsTheConsole()
    {
        // Port 1 refuses instantly, so no grant can ever be delivered and the
        // queue is the only thing that grows. It must stay bounded and report
        // the loss instead of pinning memory for the rest of the run.
        var provisioner = new TelnetProvisioner(
            () => new TelnetAdmin("127.0.0.1", 1, password: "", log: null),
            _ => { });
        for (int i = 0; i < TelnetProvisioner.MaxQueued * 3; i++)
            provisioner.Enqueue($"give {i} thrownDynamite 3", i, null);
        provisioner.Dispose();

        Assert.True(provisioner.Dropped > 0, "overflowing the queue lost work without reporting it");
    }
}
