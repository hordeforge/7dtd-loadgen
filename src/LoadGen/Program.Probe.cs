namespace SevenDTD.LoadGen;

public static partial class Program
{
    static int RunProbe(string[] args)
    {
        string host = "127.0.0.1";
        // Bots (probe included) speak LiteNetLib and must target the data port =
        // ServerPort + 2 (26902 for the stock 26900 server); a probe on the game
        // client port gets no protocol response and fails.
        int port = 26902;
        string key = Environment.GetEnvironmentVariable("LOADGEN_KEY") is { Length: > 0 } k ? k : "";
        int timeoutMs = 8000;
        string? logPath = null;
        int clientId = 1;
        int count = 1;
        int concurrency = 0;
        double minPassRate = 0.95;
        bool quiet = false;
        int rampMs = 0;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--host" && i + 1 < args.Length) host = args[++i];
            else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else if (args[i] == "--key") return SecretFlagRemoved(args[i], "LOADGEN_KEY");
            else if (args[i] == "--timeout" && i + 1 < args.Length) timeoutMs = int.Parse(args[++i]);
            else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
            else if (args[i] == "--id" && i + 1 < args.Length) clientId = int.Parse(args[++i]);
            else if (args[i] == "--count" && i + 1 < args.Length) count = int.Parse(args[++i]);
            else if (args[i] == "--concurrency" && i + 1 < args.Length) concurrency = int.Parse(args[++i]);
            else if (args[i] == "--min-pass-rate" && i + 1 < args.Length) minPassRate = double.Parse(args[++i]);
            // Clamp like the join parser: the ramp delay cast must not overflow.
            else if (args[i] == "--ramp-ms" && i + 1 < args.Length) rampMs = Math.Clamp(int.Parse(args[++i]), 0, 3_600_000);
            else if (args[i] == "--quiet") quiet = true;
        }

        if (count < 1) count = 1;
        if (!IsValidPort(port))
            return InvalidArg("--port", port.ToString(), "an integer 1..65535");
        if (!IsValidMinPassRate(minPassRate))
            return InvalidArg("--min-pass-rate", minPassRate.ToString(), "a fraction between 0 and 1");
        if (timeoutMs <= 0)
            return InvalidArg("--timeout", timeoutMs.ToString(), "a positive millisecond value");
        if (count == 1)
        {
            Action<string>? log = quiet ? null : Console.WriteLine;
            var result = LiteNetProbe.Run(host, port, key, timeoutMs, clientId, log);
            if (!string.IsNullOrEmpty(logPath))
                WriteArtifact("log", logPath, () => File.WriteAllLines(logPath, result.Lines));
            if (!result.Pass)
            {
                Console.WriteLine($"[{DateTime.UtcNow:O}] [fake#{clientId}] FAIL: no LiteNetLib protocol progress");
                return 1;
            }
            Console.WriteLine($"[{DateTime.UtcNow:O}] [fake#{clientId}] PASS: protocol progress beyond socket open");
            return 0;
        }

        concurrency = LoadRunner.ResolveConcurrency(concurrency, count);
        Console.WriteLine(
            $"[{DateTime.UtcNow:O}] LOAD start host={host} port={port} count={count} " +
            $"concurrency={concurrency} timeoutMs={timeoutMs} minPassRate={minPassRate:P0}");
        var summary = LoadRunner.Run(host, port, key, timeoutMs, count, concurrency, rampMs, quiet, idBase: clientId);
        if (!string.IsNullOrEmpty(logPath))
            WriteArtifact("log", logPath, () => File.WriteAllText(logPath, summary.ToReport()));
        Console.WriteLine(summary.ToReport());
        if (!Program.JoinGatePass(summary.Pass, summary.Total, minPassRate))
        {
            Console.WriteLine($"FAIL: passRate={summary.PassRate:P2} < minPassRate={minPassRate:P2}");
            return 1;
        }
        Console.WriteLine($"PASS: load {summary.Total} clients passRate={summary.PassRate:P2}");
        return 0;
    }
}
