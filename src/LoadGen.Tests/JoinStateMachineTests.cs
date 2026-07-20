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
