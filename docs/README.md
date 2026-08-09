# 7dtd-loadgen documentation

**Owns:** doc index for this project.  
**Not:** product RealEarth design (sibling `7days-realworld`).

| Doc | Role |
|---|---|
| [`../README.md`](../README.md) | Build, join, dedicated worlds, workload controls |
| [`REALEARTH.md`](REALEARTH.md) | RealEarth dedicated bot scenarios |
| [`../TODO.md`](../TODO.md) | Protocol and workload backlog |
| [`../AGENTS.md`](../AGENTS.md) | Agent / project rules |

## Sibling docs (evidence loop)

| Doc | Role |
|---|---|
| APM | [`../../7dtd-apm/docs/APM.md`](../../7dtd-apm/docs/APM.md) |
| Canonical load profile | [`../../7dtd-apm/docs/LOAD_PROFILE.md`](../../7dtd-apm/docs/LOAD_PROFILE.md) |
| Host topology | [`../../7dtd-optimizer/docs/HOST_TUNING.md`](../../7dtd-optimizer/docs/HOST_TUNING.md) |
| RealEarth product hub | [`../../7days-realworld/docs/INDEX.md`](../../7days-realworld/docs/INDEX.md) |
| Measured scale laws | [`../../7dtd-optimizer/docs/measured-scaling.md`](../../7dtd-optimizer/docs/measured-scaling.md) |

Prefer the root README for day-to-day operators. Canonical **wire RE** for clone work lives in research:

| Doc | Role |
|---|---|
| [`../../7dtd-research/docs/protocol.md`](../../7dtd-research/docs/protocol.md) | Envelope, join, golden package bodies |
| [`../../zdtd/docs/ZIG_CLONE.md`](../../zdtd/docs/ZIG_CLONE.md) | High-perf Zig dedi architecture |

Source of golden layouts: `src/LoadGen/PackageCodec.cs` (`--golden-wire`).

## Changelog

- **2026-07-19:** Expanded hub with sibling evidence-loop links.
