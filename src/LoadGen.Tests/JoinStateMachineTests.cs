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

    [Fact]
    public void TryGetTypeName_ReverseLookup_MatchesForwardMap()
    {
        var sm = new JoinStateMachine();
        sm.ApplyPackageMappings(new[] { "NetPackagePackageIds", "NetPackagePlayerLogin", "", "NetPackageChat" });

        // Every forward entry resolves back through the O(1) reverse map.
        foreach (var kv in sm.PackageIds)
        {
            Assert.True(sm.TryGetTypeName(kv.Value, out var name));
            Assert.Equal(kv.Key, name);
        }
        // Empty mapping slots resolve to nothing.
        Assert.False(sm.TryGetTypeName(2, out _));
        Assert.False(sm.TryGetTypeName(999, out _));
    }

    [Fact]
    public void ApplyPackageMappings_Reapply_DropsStaleReverseIds()
    {
        var sm = new JoinStateMachine();
        sm.ApplyPackageMappings(new[] { "Old1", "Old2" });
        Assert.True(sm.TryGetTypeName(1, out _));

        // A second PackageIds packet remaps everything; stale ids must not
        // survive in the reverse map (it mirrors the cleared forward map).
        sm.ApplyPackageMappings(new[] { "New1" });
        Assert.False(sm.TryGetTypeName(1, out _));
        Assert.True(sm.TryGetTypeName(0, out var name));
        Assert.Equal("New1", name);
    }

    [Fact]
    public void Note_BeyondCap_RetainsNewestLines_Only()
    {
        var sm = new JoinStateMachine();
        for (int i = 0; i < 9000; i++)
            sm.Note($"line {i}");

        // Retention stays bounded for multi-hour soak sessions...
        Assert.True(sm.Log.Count <= 4000);
        // ...and the newest lines survive the trim.
        Assert.Contains("line 8999", sm.Log[^1]);
        Assert.Equal("line 8999", sm.Log[^1]);
    }

    [Fact]
    public void AddCounters_FoldsEveryCounter_IncludingRejoins()
    {
        // Attempt states carry RejoinCount 0 (only the session total increments
        // it directly), so folding it must be a no-op there - and the fold must
        // never silently skip a counter the state exposes.
        var totals = new JoinStateMachine { RejoinCount = 4 };
        var attempt = new JoinStateMachine
        {
            WalkActions = 10,
            JumpActions = 2,
            CrouchActions = 1,
            AimActions = 3,
            TurnActions = 5,
            StrafeActions = 2,
            LookActions = 1,
            ChatActions = 4,
            BreakBlockActions = 6,
            AttackActions = 7,
            DrownActions = 8,
            SuicideActions = 9,
            KilledActions = 1,
            DeathCount = 2,
            RespawnCount = 1,
            RejoinCount = 0,
        };

        totals.AddCounters(attempt);

        Assert.Equal(10, totals.WalkActions);
        Assert.Equal(2, totals.JumpActions);
        Assert.Equal(1, totals.CrouchActions);
        Assert.Equal(3, totals.AimActions);
        Assert.Equal(5, totals.TurnActions);
        Assert.Equal(2, totals.StrafeActions);
        Assert.Equal(1, totals.LookActions);
        Assert.Equal(4, totals.ChatActions);
        Assert.Equal(6, totals.BreakBlockActions);
        Assert.Equal(7, totals.AttackActions);
        Assert.Equal(8, totals.DrownActions);
        Assert.Equal(9, totals.SuicideActions);
        Assert.Equal(1, totals.KilledActions);
        Assert.Equal(2, totals.DeathCount);
        Assert.Equal(1, totals.RespawnCount);
        Assert.Equal(4, totals.RejoinCount); // unchanged by an attempt with 0
    }

    [Fact]
    public void SetCounters_CopiesWholeSessionSnapshot()
    {
        // RunWithRejoin's final fold: the last attempt snapshot reports the
        // whole session. The copy must be a value copy: later mutations of the
        // aggregate must not leak into an already-published snapshot.
        var totals = new JoinStateMachine { WalkActions = 42, RejoinCount = 3 };
        var last = new JoinStateMachine();

        last.SetCounters(totals);
        Assert.Equal(42, last.WalkActions);
        Assert.Equal(3, last.RejoinCount);

        totals.WalkActions += 1;
        Assert.Equal(42, last.WalkActions);
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
