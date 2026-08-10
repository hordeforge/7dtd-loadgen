using SevenDTD.LoadGen;
using Xunit;

namespace SevenDTD.LoadGen.Tests;

/// <summary>L11: monotonic join-stage invariants for JoinStateMachine.Advance.</summary>
public sealed class JoinStateMachineTests
{
    [Fact]
    public void Advance_DoesNotRegress_ExceptToTerminal()
    {
        var sm = new JoinStateMachine();
        sm.Advance(JoinStage.LoginSent);
        Assert.Equal(JoinStage.LoginSent, sm.Stage);

        // Lower stage is ignored (no regression).
        sm.Advance(JoinStage.UdpOpen);
        Assert.Equal(JoinStage.LoginSent, sm.Stage);

        // Terminal is always reachable, even though it is a lower-numbered special case.
        sm.Advance(JoinStage.Disconnected);
        Assert.Equal(JoinStage.Disconnected, sm.Stage);

        // Once terminal, further advances are frozen.
        sm.Advance(JoinStage.Joined);
        Assert.Equal(JoinStage.Disconnected, sm.Stage);
    }

    [Fact]
    public void Fail_IsNoOp_AfterJoined()
    {
        var sm = new JoinStateMachine();
        sm.MarkJoined();
        Assert.Equal(JoinStage.Joined, sm.Stage);

        sm.Fail("should be ignored");
        Assert.Equal(JoinStage.Joined, sm.Stage);
        Assert.Null(sm.FailReason);
    }

    [Fact]
    public void Fail_SetsTerminal_BeforeJoined()
    {
        var sm = new JoinStateMachine();
        sm.Advance(JoinStage.LoginSent);
        sm.Fail("boom");
        Assert.Equal(JoinStage.Failed, sm.Stage);
        Assert.Equal("boom", sm.FailReason);
        Assert.True(sm.IsTerminal);
    }

    [Fact]
    public void ApplyPackageMappings_SkipsEmpties_MapsByIndex()
    {
        var sm = new JoinStateMachine();
        sm.ApplyPackageMappings(new[] { "", "Alpha", "", "Bravo" });

        Assert.Equal(2, sm.PackageIds.Count);
        Assert.True(sm.TryGetPackageId("Alpha", out var a));
        Assert.Equal((ushort)1, a);
        Assert.True(sm.TryGetPackageId("Bravo", out var b));
        Assert.Equal((ushort)3, b);
        Assert.False(sm.PackageIds.ContainsKey(""));
        Assert.Equal(JoinStage.PackageIdsReceived, sm.Stage);
    }
}

/// <summary>
/// Rejoin contract: a per-attempt JoinStateMachine that hits a terminal
/// pre-join state is discarded and a fresh one starts the next attempt; a
/// joined machine is frozen and never retried. This models RunWithRejoin's
/// loop (new machine per attempt until the session wall clock expires).
/// </summary>
public sealed class RejoinPolicyTests
{
    [Fact]
    public void FreshMachine_StartsClean_AfterTerminalFail()
    {
        var attempt1 = new JoinStateMachine();
        attempt1.Advance(JoinStage.PackageIdsReceived);
        attempt1.Fail("kick");
        Assert.True(attempt1.IsTerminal);

        // Retry = brand new machine, no state carried over.
        var attempt2 = new JoinStateMachine();
        Assert.Equal(JoinStage.Created, attempt2.Stage);
        Assert.False(attempt2.IsTerminal);
        Assert.Empty(attempt2.PackageIds);
    }

    [Fact]
    public void JoinedMachine_IsFrozen_NoRetry()
    {
        var sm = new JoinStateMachine();
        sm.MarkJoined();
        sm.Fail("late-kick"); // Fail is a no-op after Joined
        Assert.Equal(JoinStage.Joined, sm.Stage);
        Assert.False(sm.IsTerminal);
        Assert.True(sm.IsJoined);
    }

    [Fact]
    public void Disconnect_AfterJoined_StillCountsAsJoined()
    {
        var sm = new JoinStateMachine();
        sm.MarkJoined();
        sm.Advance(JoinStage.Disconnected); // terminal, but EverJoined recorded
        Assert.Equal(JoinStage.Disconnected, sm.Stage);
        Assert.True(sm.IsJoined); // EverJoined survives the terminal transition
    }
}
