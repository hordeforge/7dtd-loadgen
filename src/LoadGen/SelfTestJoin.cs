using System.Diagnostics;

namespace SevenDTD.LoadGen;

/// <summary>In-process join against <see cref="MockGameServer"/> (shipped path
/// for CI). Lives beside the mock host it drives rather than on
/// <see cref="GameJoinClient"/>, which is the join client and not a test host.</summary>
public static class SelfTestJoin
{
    public static int Run(int actionCount, int seed, Action<string>? log, out JoinStateMachine sm)
    {
        using var server = new MockGameServer(seed);
        server.Start(0);
        using var cts = new CancellationTokenSource();
        var poll = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                server.Poll();
                Thread.Sleep(2);
            }
        });

        var client = new GameJoinClient();
        // CI: die (client drown for mock), respawn, walk again. Live default is still no self-kill.
        int rc = client.Run(new GameJoinClient.Options
        {
            Host = "127.0.0.1",
            Port = server.Port,
            PlayerName = "REFake",
            TimeoutMs = 25_000,
            ActionCount = Math.Max(actionCount, 12),
            ActionSeed = seed,
            ClientId = 1,
            Mode = ActionLoop.BotMode.Wander,
            Death = ActionLoop.DeathMethod.Drown,
            Respawn = true,
            MaxLives = 2,
            RespawnDelayMs = 100,
            CohortSize = 1,
            PaceMs = 5, // fast for CI
            Log = log,
        });
        sm = client.State;

        // Drain (monotonic: immune to wall-clock steps during the window).
        // Do NOT call server.Poll() here: the background poller below is still
        // running, and concurrent PollEvents() on one NetManager would dispatch
        // its receive handlers on both threads at once.
        var drain = Stopwatch.StartNew();
        while (drain.ElapsedMilliseconds < 300) { Thread.Sleep(5); }
        cts.Cancel();
        try
        {
            poll.Wait(1000);
        }
        catch (AggregateException ex)
        {
            // A dead mock poller stalls the join (no challenge/login answer is
            // ever serviced); the cause must be visible instead of surfacing as
            // an unexplained 25s client timeout.
            var baseEx = ex.GetBaseException();
            log?.Invoke($"FAIL self-test poller faulted: {baseEx.GetType().Name}: {baseEx.Message}");
        }

        log?.Invoke(
            $"SELFTEST server walksRecv={server.WalkPackages} jumpsRecv={server.JumpPackages} " +
            $"flagsRecv={server.FlagPackages} chatRecv={server.ChatPackages} lookRecv={server.LookPackages} " +
            $"drownsRecv={server.DrownPackages} suicidesRecv={server.SuicidePackages} killsRecv={server.KillPackages} " +
            $"logins={server.LoginsAccepted} challengesOk={server.ChallengesOk} " +
            $"deaths={sm.DeathCount} respawns={sm.RespawnCount}");

        if (rc != 0 || !sm.EverJoined)
            return 1;
        if (sm.WalkActions < 1)
        {
            log?.Invoke($"FAIL actions walks={sm.WalkActions}");
            return 1;
        }
        if (server.WalkPackages < 1)
        {
            log?.Invoke("FAIL server did not observe walk packages");
            return 1;
        }
        // Pins the V3.2.0 damage-body offsets the mock decodes: the drown
        // damage source/type only land in these counters if the mock reads
        // them from behind the packed flags word, not from the flags bytes.
        if (server.DrownPackages < sm.DeathCount)
        {
            log?.Invoke($"FAIL mock saw {server.DrownPackages} drown packages for {sm.DeathCount} deaths");
            return 1;
        }
        if (sm.DeathCount < 2)
        {
            log?.Invoke($"FAIL expected 2 deaths for respawn loop, got {sm.DeathCount}");
            return 1;
        }
        if (sm.RespawnCount < 1)
        {
            log?.Invoke($"FAIL expected respawn after first death, got {sm.RespawnCount}");
            return 1;
        }
        return 0;
    }
}
