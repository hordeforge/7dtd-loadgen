using LiteNetLib;

namespace SevenDTD.LoadGen;

static class SelfTest
{
    public static int Run(string[] args)
    {
        int port = 0, count = 1, concurrency = 0, timeoutMs = 4000;
        double minPassRate = 0.95;
        string? logPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
            else if (args[i] == "--count" && i + 1 < args.Length) count = int.Parse(args[++i]);
            else if (args[i] == "--concurrency" && i + 1 < args.Length) concurrency = int.Parse(args[++i]);
            else if (args[i] == "--min-pass-rate" && i + 1 < args.Length) minPassRate = double.Parse(args[++i]);
            else if (args[i] == "--timeout" && i + 1 < args.Length) timeoutMs = int.Parse(args[++i]);
        }
        if (count < 1) count = 1;
        // Port 0 = pick an ephemeral port for the in-process host.
        if (port != 0 && !Program.IsValidPort(port))
            return Program.InvalidArg("--port", port.ToString(), "0 (ephemeral) or an integer 1..65535");
        if (!Program.IsValidMinPassRate(minPassRate))
            return Program.InvalidArg("--min-pass-rate", minPassRate.ToString(), "a fraction between 0 and 1");
        if (timeoutMs <= 0)
            return Program.InvalidArg("--timeout", timeoutMs.ToString(), "a positive millisecond value");
        concurrency = LoadRunner.ResolveConcurrency(concurrency, count);

        var serverListener = new EventBasedNetListener();
        var server = new NetManager(serverListener) { AutoRecycle = true, UpdateTime = 15 };
        serverListener.ConnectionRequestEvent += req => req.Accept();
        // Loopback-only bind: the host accepts every connection unconditionally,
        // so a wildcard bind would expose an unauthenticated game-protocol echo
        // service to the local network for the test's duration. The probe and
        // cohort both target 127.0.0.1, which the loopback bind serves.
        if (!server.Start(System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback, port < 0 ? 0 : port))
        {
            Console.Error.WriteLine(
                "FAIL: self-test host could not listen on loopback"
                + (port > 0 ? $" port {port} (already in use?)" : " (ephemeral port)"));
            return 1;
        }
        port = port > 0 ? port : server.LocalPort;
        Console.WriteLine($"[{DateTime.UtcNow:O}] [self-test] STAGE self_host_listen: port={port} count={count}");
        using var cts = new CancellationTokenSource();
        var hostLoop = Task.Run(() => { while (!cts.Token.IsCancellationRequested) { server.PollEvents(); Thread.Sleep(2); } });

        bool pass;
        if (count == 1)
        {
            var result = LiteNetProbe.Run("127.0.0.1", port, "", timeoutMs, 99, Console.WriteLine);
            pass = result.Pass && (result.Connected || result.Stages.Contains("litenet_peer_connected"));
        }
        else
        {
            var summary = LoadRunner.Run("127.0.0.1", port, "", Math.Max(timeoutMs, 6000), count, concurrency,
                rampMs: Math.Min(2000, count), quiet: true);
            Console.WriteLine(summary.ToReport());
            pass = Program.JoinGatePass(summary.Pass, summary.Total, minPassRate)
                && Program.JoinGatePass(summary.Connected, summary.Total, Math.Min(minPassRate, 0.90));
        }
        cts.Cancel();
        Program.AwaitTeardown("self_host", hostLoop);
        server.Stop();
        if (!string.IsNullOrEmpty(logPath))
            Program.WriteArtifact("log", logPath, () => File.WriteAllText(logPath, $"self-test pass={pass} count={count}\n"));
        Console.WriteLine(pass ? $"PASS: self-test count={count}" : "FAIL: self-test");
        return pass ? 0 : 1;
    }
}
