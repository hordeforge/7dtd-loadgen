using System.Diagnostics;
using LiteNetLib;

namespace SevenDTD.LoadGen;

/// <summary>
/// 7DTD load-test client: LiteNetLib probe, full join path, bot walk/death/respawn.
/// </summary>
public static partial class Program
{
    /// <summary>No-op logger so game LiteNetLib never calls UnityEngine.Debug under pure .NET.</summary>
    sealed class NullNetLogger : INetLogger
    {
        public void WriteNet(NetLogLevel level, string str, params object[] args) { }
    }

    /// <summary>
    /// Stagger delay for bot i of count under --ramp-ms. Linear ramp across the
    /// cohort; first bot always starts at 0. Clamped so the Task.Delay(int)
    /// cast at scale cannot overflow (see --ramp-ms parse). Validated live
    /// 2026-08-10: 24 bots at 3000 ms ramp avoided the stock LiteNetLib
    /// join-churn race entirely (0 drops vs 302 non-ramped).
    /// </summary>
    public static int RampDelayMs(int botIndex, int count, int rampMs)
    {
        if (rampMs <= 0 || count <= 1) return 0;
        return (int)Math.Min(int.MaxValue, (long)botIndex * rampMs / (count - 1));
    }

    /// <summary>
    /// Join gate: pass when the successful-client rate meets --min-pass-rate.
    /// The 1e-9 epsilon absorbs float division rounding at exact equality.
    /// </summary>
    public static bool JoinGatePass(int passed, int total, double minPassRate)
    {
        double rate = total == 0 ? 0 : (double)passed / total;
        return rate + 1e-9 >= minPassRate;
    }

    /// <summary>Valid UDP/TCP port range for --port/--telnet-port.</summary>
    public static bool IsValidPort(int port) => port >= 1 && port <= 65535;

    /// <summary>Consumer-facing build identity, e.g. "7dtd-loadgen 0.1.1".
    /// Backed by &lt;Version&gt; in LoadGen.csproj (see test_release_contract.py).</summary>
    public static string VersionLine()
    {
        var v = typeof(Program).Assembly.GetName().Version;
        return $"7dtd-loadgen {(v is null ? "unknown" : v.ToString(3))}";
    }

    /// <summary>--min-pass-rate is a client fraction; outside [0,1] the gate
    /// silently loses meaning (always-fail or always-pass).</summary>
    public static bool IsValidMinPassRate(double rate) => !double.IsNaN(rate) && rate >= 0.0 && rate <= 1.0;

    /// <summary>Fail fast on an out-of-range configuration value instead of a
    /// confusing mid-run failure. Exit code 2 matches bad argument values.</summary>
    internal static int InvalidArg(string flag, string value, string requirement)
    {
        Console.Error.WriteLine($"FAIL: invalid {flag} '{value}': {requirement} (see --help)");
        return 2;
    }

    static int Main(string[] args)
    {
        // Game LiteNetLib logs via UnityEngine.Debug when Logger is null; pure .NET crashes
        // with "ECall methods must be packaged into a system module" on bind/socket errors.
        NetDebug.Logger = new NullNetLogger();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => GameJoinClient.DisconnectAllActive();

        Console.CancelKeyPress += (_, _) => GameJoinClient.DisconnectAllActive();

        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return 0;
        }

        if (args.Any(a => a is "-V" or "--version"))
        {
            Console.WriteLine(VersionLine());
            return 0;
        }

        if (args.Any(a => a == "--golden-wire"))
        {
            var err = PackageCodec.AssertGoldenWireLayouts();
            if (err != null)
            {
                Console.WriteLine($"FAIL golden-wire: {err}");
                return 1;
            }
            Console.WriteLine(
                "PASS golden-wire: " +
                $"PosAndRot body={PackageCodec.GoldenBodySize.EntityPosAndRotNoQ} " +
                $"RelPos body={PackageCodec.GoldenBodySize.EntityRelPosAndRotNoQ} (Int16 rot) " +
                $"AliveFlags body={PackageCodec.GoldenBodySize.EntityAliveFlags}");
            return 0;
        }

        string mode = "probe";
        if (args.Any(a => a == "--join")) mode = "join";
        if (args.Any(a => a == "--self-test")) mode = "self-test";
        if (args.Any(a => a == "--self-test-join")) mode = "self-test-join";

        try
        {
            return mode switch
            {
                "self-test-join" => RunSelfTestJoin(args),
                "self-test" => SelfTest.Run(args),
                "join" => RunJoin(args),
                _ => RunProbe(args),
            };
        }
        // CLI boundary: a malformed numeric flag (--count abc, --port 99999999999)
        // must fail as a clean usage error, not an unhandled-exception stack
        // trace. Parse sites use int/double.Parse, whose failure modes are
        // exactly these two types; wider exception families stay visible.
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            Console.Error.WriteLine($"FAIL: bad argument value: {ex.Message} (see --help)");
            return 2;
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine(
            "7dtd-loadgen: LiteNetLib probe + full join + bot actions\n" +
            "Modes:\n" +
            "  (default) probe     LiteNetLib connectivity only\n" +
            "  --join              Full join path + bot action loop\n" +
            "  --self-test         In-process LiteNetLib host+probe (scale with --count)\n" +
            "  --self-test-join    In-process mock 7DTD join + actions (CI gate)\n" +
            "\n" +
            "Join bot flags:\n" +
            "  --profile probe|join-burst|steady-wander|death-soak|mixed|bench\n" +
            "      preset cohort defaults; explicit flags override per key\n" +
            "      bench = ramped wander cohort + warm-up + measurement window\n" +
            "  --bench-warmup-ms N   bench warm-up before the window (default 30000)\n" +
            "  --bench-window-ms N   bench measurement window; >0 enables the bench\n" +
            "      summary (stats-json bench block + BENCH_SUMMARY line)\n" +
            "  --mode wander|mixed|chatty|combat|patrol|chaos|demolition|bait|kite|traverse\n" +
            "      default: wander (walk until world death)\n" +
            "  --death none|drown|suicide|killed|random\n" +
            "      default none: never self-kill; wait for world death\n" +
            "  --actions N         live steps (0 or omit = endless until death/timeout)\n" +
            "  --respawn / --no-respawn   after death request spawn and walk again (default on)\n" +
            "  --max-lives N       stop after N deaths (0 = unlimited until --timeout)\n" +
            "  --respawn-delay-ms N  wait after death before respawn (default 1500)\n" +
            "  --respawn-timeout-ms N  max wait for server to confirm respawn (default 40000)\n" +
            "  --spawn-zombies     telnet-spawn zombies near bots (default on)\n" +
            "  --no-spawn-zombies  disable telnet spawns\n" +
            "  --telnet-host/port/password  dedicated telnet (default 127.0.0.1:8081 retest)\n" +
            "  --pace-ms N --seed N --name NAME --count N --concurrency N\n" +
            "  --mixed-actions     mixed walk/jump/turn/crouch steps instead of pure wander\n" +
            "  --bot-mix m1:w1,m2:w2  weighted per-bot modes; overrides --mode\n" +
            "  --max-dynamite N    dynamite charges per life (default 3, demolition 200)\n" +
            "  --spawn-entity LIST --spawn-per-player N --spawn-every-ms N\n" +
            "      comma entity classes spawned near bots via telnet (default zombieBoe)\n" +
            "  --horde-every-ms N --horde-waves N  wandering-horde bursts (0 = off)\n" +
            "  --kill-fallback / --no-kill-fallback\n" +
            "      admin kill when se finds no spawn point (default on)\n" +
            "  --stats-json PATH   cohort summary (schema 7dtd.loadgen.stats.v1)\n" +
            "  --run-manifest PATH run manifest (schema 7dtd.loadgen.run.v1)\n" +
            "  --id N --scenario-id ID  base client id / scenario tag for artifacts\n" +
            "  --host --port --timeout --log --min-pass-rate --no-actions --ramp-ms --quiet\n" +
            "  --observe-cvar NAME  observe one exact replicated CVar (repeatable)\n" +
            "  --observe-buff NAME  observe one exact replicated buff (repeatable)\n" +
            "  --events-jsonl PATH  write filtered joined/state events as JSON lines\n" +
            "  --golden-wire       Assert package body layouts vs Assembly-CSharp IL sizes\n" +
            "  -V / --version      print client version and exit\n" +
            "Notes:\n" +
            "  Walk → world kill → DEATH → respawn → walk again until --timeout. No self-kill.\n" +
            "  Default timeout 1 hour. Rejoins on early disconnect. Telnet zed spawn for empty worlds.\n" +
            "Secrets via environment (flags override; avoids ps-visible argv):\n" +
            "  LOADGEN_KEY              server join password (--key/--password fallback)\n" +
            "  LOADGEN_TELNET_PASSWORD  admin telnet password (--telnet-password fallback)\n" +
            "Examples:\n" +
            "  7dtd-loadgen --join --host 127.0.0.1 --port 26902 --count 8\n" +
            "  7dtd-loadgen --join --count 4 --timeout 1800000 --max-lives 10\n" +
            "  7dtd-loadgen --self-test-join --actions 24\n" +
            "  7dtd-loadgen --golden-wire\n");
    }
}
