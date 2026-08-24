using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

public sealed class TelnetPressureLoopTests
{
    [Fact]
    public async Task CancellationMidInterval_EndsCleanlyWithoutFaulting()
    {
        // Teardown cancels the token while the loop sits in the inter-wave
        // delay. The task must stop promptly AND end non-faulted: AwaitTeardown
        // reports every faulted task as an ERROR line, so a fault here would
        // stamp spurious errors into every clean run's log.
        using var cts = new CancellationTokenSource();
        var adminCreated = new ManualResetEventSlim();
        var task = Program.RunTelnetPressureLoop(
            "test", cts.Token,
            startDelayMs: 0,
            intervalMs: 60_000,
            errorBackoffMs: 60_000,
            // Port 1 refuses instantly on loopback: Connect fails, wave never runs.
            createAdmin: () =>
            {
                adminCreated.Set();
                return new TelnetAdmin("127.0.0.1", 1, password: "", log: null);
            },
            wave: _ => throw new InvalidOperationException("wave must not run"));

        Assert.True(adminCreated.Wait(10_000), "pressure loop never started");
        Thread.Sleep(200); // let it settle into the interval wait
        cts.Cancel();

        var done = await Task.WhenAny(task, Task.Delay(5_000));
        Assert.Same(task, done);
        Assert.False(task.IsFaulted, $"pressure task faulted: {task.Exception?.GetBaseException().Message}");
    }

    [Fact]
    public async Task PreCancelledToken_NeverConnectsOrFaults()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var task = Program.RunTelnetPressureLoop(
            "test", cts.Token,
            startDelayMs: 0, intervalMs: 1_000, errorBackoffMs: 1_000,
            () => new TelnetAdmin("127.0.0.1", 1, password: "", log: null),
            _ => throw new InvalidOperationException("wave must not run"));

        var done = await Task.WhenAny(task, Task.Delay(5_000));
        Assert.Same(task, done);
        Assert.False(task.IsFaulted);
    }
}
