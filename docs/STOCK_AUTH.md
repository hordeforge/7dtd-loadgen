# Stock auth model and the no-Steam test-server question

How the stock dedicated validates joins, and the two ways to run the real
stock client against a test server without valid Steam auth. RE ground truth:
`../7dtd-research/docs/platform-auth.md` (authorizer chain, Steam/EOS/Local
platforms). This doc is the operational decision for the harness.

## How join auth works (stock V3.1.0)

A joining player carries a platform identity + auth ticket
(`PlatformUserIdentifierAbs` + token in the login package). The server runs a
reflection-discovered **authorizer chain**; each authorizer either passes
(SyncAllow / WaitAsync -> async callback), fails (SyncDeny -> kick), or is
skipped (platform mismatch or `AuthorizerActive == false`):

- Steam: `NativePlatformAuthorizer` -> `AuthenticationServer.AuthenticateUser`
  -> `SteamGameServer.BeginAuthSession(ticket)`; the verdict is the async
  `ValidateAuthTicketResponse`. Invalid ticket -> kick
  (`EKickReason.PlatformAuthenticationFailed`, the client shows "Platform auth
  failed: InvalidTicket"). There is **no stock serverconfig that deactivates
  this authorizer** (unlike EAC, which `EACEnabled=false` disables).
- EAC: skipped when `EACEnabled=false` (our test servers).
- EOS cross-play: `CrossplatformAuthorizer` runs only when cross-play is on.

`Platform.Local` and `Platform.LAN` are first-class platforms with their own
factories and (for Local) trivial auth: **no ticket**. The stock dedicated with
`serverplatforms=Steam,LAN,Local,` accepts Local clients without any mod - the
loadgen bots do this every run (`PltfmId='Local_REFake1'`).

## The problem

The real stock client is a Steam-platform client (its own `platform.cfg` says
`platform=Steam, crossplatform=EOS`). It presents a Steam ticket; the server
must validate it via Steamworks. When Steam's session is offline/stale, the
ticket is invalid and the client is kicked. Observed live 2026-08-12:
`[NET] Kicked from server: Platform auth failed: InvalidTicket` at 15:19,
recovered at 15:22 once a synthetic-auth bypass was active.

## Option A: Local/LAN client (no server mod, no Steam)

The server already accepts Local clients. If the **client's** `platform.cfg`
selects Local (and EOS crossplay off), the client initializes `Platform.Local`
and joins as `Local_<name>` - no ticket, no Steam dependency, exactly the
fully-local-network model.

- `7dtd-connect` ships the switch: `CLIENT_PLATFORM=local ./scripts/launch_client.sh`
  backs up the game's `platform.cfg`, selects `platform=Local,
  crossplatform=None`, and restores on exit (self-healing after a hard kill).
  Playtest launches flow the env through, so `CLIENT_PLATFORM=local make
  playtest-...` uses it too.
- **Live-verified 2026-08-12**: the real client (Proton) with
  `CLIENT_PLATFORM=local` joined a stock dedicated as `PltfmId='Local_maci'`
  with the full auth chain passing (PlayerSlots, PlatformAuth, BansAndWhitelist,
  Crossplay, Encryption, Finalizer) and reached `PlayerSpawnedInWorld`
  (EntityID 177). Steam DRM did not gate the Proton launch; the game honored
  `platform=Local`.
- Identity changes `Steam_...` -> `Local_...`; player records/saves key off it
  (fine for disposable test servers).
- Cleanest architecture: zero mods, zero Steam. No server-side bypass needed.

## Option B: server-side Steam-auth bypass mod

A Harmony patch on the server's Steam auth
(`AuthenticationServer.AuthenticateUser`) that auto-passes loopback/synthetic
SteamIds (`7dtd-clanker` ships this: `Patch_SteamAuthServer_SyntheticBypass`; the
parallel FPS-bot session runs it, log line `[BotMod] synthetic auth bypass for
SteamId=...`).

- Keeps the client 100 % stock (Steam identity, tickets attempted but not
  validated).
- It is a mod on the dedicated (extra moving part); the generic authorizer
  bypass was found too broad and narrowed to the concrete Steam auth server
  patch.

## Decision

Try Option A first: flip the client `platform.cfg` to `platform=Local,
crossplatform=None` and join the stock dedicated (watch the `[Auth]` lines for
`Local_<name>`). If the client refuses Local mode, Option B (the bypass mod) is
the fallback. The loadgen bots already ride the Local path, so the harness
itself never needs either option.

## Join-path errors seen on the harness (observed, not IL-verified)

Two server-side disconnect messages surfaced during the FPS-bot session while
its Steam-auth + BotMod client was connecting; both are join-path rejections,
not engine warts:

- `[NET] Kicked from server: Platform auth failed: InvalidTicket` - Steam
  ticket invalid/offline (the Option A/B problem above; documented in this
  file). Recovered once the synthetic-auth bypass was active.
- `server disconnect player name can not be empty` - a join presenting an
  empty player name is rejected at login. The exact validation IL is not yet
  pinned (searched ConnectionManager/AuthorizationManager dumps; the string
  lives elsewhere, likely the client-side login or a dedicated validation not
  in the current dumps). Harness contract: every bot must carry a non-empty
  name - loadgen does (`Local_REFake1` etc.), the playtest Local client joins
  as `Local_maci`.

If a harness client ever produces this message, the cause is an empty-name
login, not a server or auth problem; fix the client's identity, not the
server.
