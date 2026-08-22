# bench-mode live validation (2026-08-22, stock V3.1.0 b14)

`--profile bench` (16 bots, ramp 15s, warmup 30s, window 60s) against the
stock dedicated server on Navezgane (fresh save, admin telnet 8084, LiteNet
26902).

Outcome: 16/16 joins PASS. stats.json bench block:
window 30000-90000ms, actionsInWindow=17231 (287.2/s), deaths 0, respawns 0,
joinRatePerSec=0.533 (16 joins over the 30s warm-up), active 0 -> 16 with
activeAtWindowStart=16 (full cohort before the window), activeAtWindowEnd=0
(ramp-down), activeCurve 146 samples.

BLOCKER FOUND + FIXED: the first run failed 16/16 with
NetPackagePlayerDenied reason=4 (VersionMismatch), custom "V 3.1.0". Commit
b5c3069 (2026-08-21) had switched the login version/compVersion from the
display form "V 3.1.0" to LongStringNoBuild "V 3.10", based on the IL reading
that VersionAuthorizer compares its own LongStringNoBuild. EMPIRICALLY the
stock V3.1.0 authorizer ACCEPTS "V 3.1.0" and KICKS "V 3.10": reverting to
VersionLongString restores 16/16 PASS. (The b5c3069 premise was evidently
validated against zdtd, not stock.) Revert + pin test committed.
