namespace SevenDTD.LoadGen;

public static partial class Program
{
    static int RunSelfTestJoin(string[] args)
    {
        int actions = 24;
        int seed = 7;
        string? logPath = null;
        string? runManifestPath = null;
        string scenarioId = Environment.GetEnvironmentVariable("LOADGEN_SCENARIO_ID") ?? "re-selftest-client-path";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--actions" && i + 1 < args.Length) actions = int.Parse(args[++i]);
            else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
            else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
            else if (args[i] == "--run-manifest" && i + 1 < args.Length) runManifestPath = args[++i];
            else if (args[i] == "--scenario-id" && i + 1 < args.Length) scenarioId = args[++i];
        }
        if (actions < 6) actions = 6;

        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }

        Log($"[{DateTime.UtcNow:O}] self-test-join actions={actions} seed={seed}");
        JoinStateMachine sm;
        int rc;
        try
        {
            rc = GameJoinClient.RunSelfTestJoin(actions, seed, Log, out sm);
        }
        catch (InvalidOperationException ex)
        {
            // The in-process mock host failed to bind its UDP socket (port
            // exhaustion, sandboxed CI): report it like every other startup
            // failure instead of an unhandled-exception stack trace.
            Log($"FAIL: in-process mock join host could not start: {ex.Message}");
            return 1;
        }
        Log($"SUMMARY stage={sm.Stage} joined={sm.IsJoined} mode={sm.BotModeName} " +
            $"walks={sm.WalkActions} jumps={sm.JumpActions} crouch={sm.CrouchActions} aim={sm.AimActions} " +
            $"turn={sm.TurnActions} chat={sm.ChatActions} attack={sm.AttackActions} " +
            $"drowns={sm.DrownActions} suicides={sm.SuicideActions} killed={sm.KilledActions} " +
            $"deaths={sm.DeathCount} respawns={sm.RespawnCount} " +
            $"died={sm.Died} cause={sm.DeathCause} entity={sm.EntityId} fail={sm.FailReason ?? "none"}");
        if (!string.IsNullOrEmpty(logPath))
            WriteArtifact("log", logPath, () => File.WriteAllLines(logPath, lines.Concat(sm.Log)));
        if (rc == 0)
            Log("PASS: self-test-join joined + actions");
        else
            Log("FAIL: self-test-join");

        if (!string.IsNullOrEmpty(runManifestPath))
        {
            var run = new Dictionary<string, object?>
            {
                ["schema"] = "7dtd.loadgen.run.v1",
                ["kind"] = "self-test-join",
                ["scenarioId"] = scenarioId,
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["rc"] = rc,
                ["pass"] = rc == 0,
                ["actions"] = actions,
                ["seed"] = seed,
                ["stage"] = sm.Stage.ToString(),
                ["joined"] = sm.IsJoined,
                ["walks"] = sm.WalkActions,
                ["deaths"] = sm.DeathCount,
                ["respawns"] = sm.RespawnCount,
                ["product"] = new Dictionary<string, object?>
                {
                    ["name"] = "RealEarth",
                    ["priorityFocus"] = "P0-P1",
                    ["offlineGate"] = true,
                },
            };
            WriteArtifact("run_manifest", runManifestPath, () =>
                File.WriteAllText(
                    runManifestPath,
                    System.Text.Json.JsonSerializer.Serialize(run, ArtifactJsonOpts) + "\n"));
        }
        return rc;
    }
}
