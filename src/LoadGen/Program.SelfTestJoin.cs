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
        bool quiet = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--actions" && i + 1 < args.Length) actions = int.Parse(args[++i]);
            else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
            else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
            else if (args[i] == "--run-manifest" && i + 1 < args.Length) runManifestPath = args[++i];
            else if (args[i] == "--scenario-id" && i + 1 < args.Length) scenarioId = args[++i];
            else if (args[i] == "--quiet") quiet = true;
        }
        var lines = new List<string>();
        // --quiet drops the console echo of progress lines only; --log still
        // gets every line, and the verdict always reaches the console.
        void Log(string m)
        {
            if (!quiet) Console.WriteLine(m);
            lines.Add(m);
        }

        if (actions < 6)
        {
            // The self-test needs one action of every kind it asserts on, so 6
            // is a floor, not a default. The clamp is named in the log so a
            // raised run is not mistaken for the one that was asked for.
            Log("NOTE: --actions below the 6-action self-test floor, raised to 6");
            actions = 6;
        }

        void Verdict(string m, bool ok)
        {
            lines.Add(m);
            (ok ? Console.Out : Console.Error).WriteLine(m);
        }

        Log($"[{DateTime.UtcNow:O}] self-test-join actions={actions} seed={seed}");
        JoinStateMachine sm;
        int rc;
        try
        {
            rc = SelfTestJoin.Run(actions, seed, Log, out sm);
        }
        catch (Exception ex)
        {
            // The mock host failed to come up, or the session faulted under it
            // (port exhaustion, a sandboxed CI with no loopback UDP, a defect
            // in the client path). Catch the whole family: this lane is the CI
            // gate, and a type the catch misses ends the gate on a stack trace
            // with no PASS/FAIL line, which reads as a crash rather than a
            // verdict. The type and the top frame go in the line so the cause
            // survives the console being gone.
            Verdict($"FAIL: in-process mock join host could not run: "
                + RunReport.FaultText("self-test-join session", ex), false);
            return 1;
        }
        Log($"SUMMARY stage={sm.Stage} joined={sm.IsJoined} mode={sm.BotModeName} " +
            $"walks={sm.WalkActions} jumps={sm.JumpActions} crouch={sm.CrouchActions} aim={sm.AimActions} " +
            $"turn={sm.TurnActions} chat={sm.ChatActions} attack={sm.AttackActions} " +
            $"drowns={sm.DrownActions} suicides={sm.SuicideActions} killed={sm.KilledActions} " +
            $"deaths={sm.DeathCount} respawns={sm.RespawnCount} " +
            $"died={sm.Died} cause={DeathCauseNames.Of(sm.DeathCause)} entity={sm.EntityId} fail={sm.FailReason ?? "none"}");
        if (!string.IsNullOrEmpty(logPath))
            RunReport.WriteArtifact("log", logPath, () => RunReport.WriteLines(logPath, lines.Concat(sm.Log)));
        Verdict(rc == 0 ? "PASS: self-test-join joined + actions" : "FAIL: self-test-join", rc == 0);

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
            RunReport.WriteArtifact("run_manifest", runManifestPath, () =>
                File.WriteAllText(
                    runManifestPath,
                    System.Text.Json.JsonSerializer.Serialize(run, ArtifactJsonOpts) + "\n"));
        }
        return rc;
    }
}
