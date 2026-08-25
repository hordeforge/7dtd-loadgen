# 7dtd-loadgen documentation

**Owns:** doc index for this project.  
**Not:** product RealEarth design (sibling `7dtd-realearth`).

| Doc | Role |
|---|---|
| [`../README.md`](../README.md) | Build, join, dedicated worlds, workload controls |
| [`../BLOODMOON.md`](../BLOODMOON.md) | Canonical worst-case load profile + measured capacity |
| [`THREAT_MODEL.md`](THREAT_MODEL.md) | Attack surface, trust boundaries, risk-ranked threats |
| [`REALEARTH.md`](REALEARTH.md) | RealEarth dedicated bot scenarios |
| [`STOCK_AUTH.md`](STOCK_AUTH.md) | Join-auth model; operational decision for Steamless client runs |
| [`SUT_COMPARE.md`](SUT_COMPARE.md) | Stock-vs-zdtd comparison harness, axes, triage record |
| [`../TODO.md`](../TODO.md) | Protocol and workload backlog |
| [`../CHANGELOG.md`](../CHANGELOG.md) | Release history (Keep a Changelog) |
| [`../AGENTS.md`](../AGENTS.md) | Agent / project rules |

## Sibling docs (evidence loop)

| Doc | Role |
|---|---|
| APM | [`../../7dtd-server-apm/docs/APM.md`](../../7dtd-server-apm/docs/APM.md) |
| Canonical load profile | [`../../7dtd-server-apm/docs/LOAD_PROFILE.md`](../../7dtd-server-apm/docs/LOAD_PROFILE.md) |
| Host topology | [`../../7dtd-server-optimizer/docs/HOST_TUNING.md`](../../7dtd-server-optimizer/docs/HOST_TUNING.md) |
| RealEarth product hub | [`../../7dtd-realearth/docs/INDEX.md`](../../7dtd-realearth/docs/INDEX.md) |
| Measured scale laws | [`../../7dtd-server-optimizer/docs/measured-scaling.md`](../../7dtd-server-optimizer/docs/measured-scaling.md) |

Prefer the root README for day-to-day operators. Canonical **wire RE** for clone work lives in research:

| Doc | Role |
|---|---|
| [`../../7dtd-engine-research/docs/protocol.md`](../../7dtd-engine-research/docs/protocol.md) | Envelope, join, golden package bodies |
| [`../../zdtd-server/docs/ZIG_CLONE.md`](../../zdtd-server/docs/ZIG_CLONE.md) | High-perf Zig dedi architecture |

Source of golden layouts: `src/LoadGen/PackageCodec.cs` (`--golden-wire`).

## Changelog

- **2026-08-26:** Added the root CHANGELOG to the index.
- **2026-08-24:** Index caught up with docs added since 2026-08-06 (STOCK_AUTH, SUT_COMPARE) and the canonical BLOODMOON profile.
- **2026-07-19:** Expanded hub with sibling evidence-loop links.
