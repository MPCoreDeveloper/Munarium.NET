# Changelog

All notable changes to this port are recorded here, newest first. The version in
`Directory.Build.props` is what a local build produces; a release takes its version from the tag
that triggers it, so the entries below are keyed by the tag.

The port's own rule is [docs/method.md](docs/method.md): a claim about the original gets read
before it gets written, and what is missing is named rather than inferred. This file follows the
same rule - every entry says what is in, and what a reader should not expect to find.

## 0.1.0-preview.1 - 2026-09-20

First preview. The kernel is the finished part and the planes around it are complete; what is left
is listed under *Not in this release* rather than left to be discovered.

### Packages

Five libraries are published, each with a symbol package and the licence, notice, credits, readme
and icon a NuGet consumer is entitled to:

| Package | What it is |
| --- | --- |
| `Munarium.Core` | The kernel: ledger, governance, claims, shapes, facts and pins, chronology, digest ladder, context composition, retrieval seams, budgets, provider contracts |
| `Munarium.Wire` | The wire contract: the JSON models and operations generated from `openapi/munarium.v1.yaml`, the protobuf message shapes, and the spec itself in `contract/` |
| `Munarium.Providers` | The provider adapters |
| `Munarium.Runbooks` | Applying and validating runbook YAML |
| `Munarium.Store.SharpCoreDb` | The SharpCoreDB storage, search, index and provider stores |

`Munarium.Server` is not published: it is the host, and a deployment builds it from source. The
packages carry source links, so a debugger steps into the exact commit a package was built from.

### Kernel

- **Ledger** over `IStorageBackend`: a head read, an optimistic-concurrency append, a stream read
  and a global read. Identities are ULIDs, so a claim's identity also says when it happened and
  how it sorts.
- **Governance in the write path**: a gate returns permitted or blocked, and a blocked claim is
  recorded as *disputed* rather than dropped.
- **The gate vocabulary**: all six rule families as pure functions of a pinned snapshot and a
  candidate unit - `anchor-consistency`, `ledger-conflict`, `orphaned-reference`, `meta-leakage`,
  `lexical-similarity` and the anchor finding that subsumes it.
- **Claims and resolution**: `subject.key=value` with its scope, provenance and the claim it
  supersedes, so a correction is a supersession chain rather than an overwrite.
- **Anchors, promises and counters**, the **digest ladder**, the **chronology algebra**,
  **declarative shapes**, and **facts and pins** that rebuild the same slice and the same SHA-256
  digest every time.
- **Context composition** within a token budget, with refused claims listed separately.

### Ingest, retrieval and evidence

- Sources, revisions, collection and prefix binding, extraction (including PDF), and the gate in
  front of the write path on every transport.
- Chunking, embeddings, full-text and vector search with fusion, query expansion, and index
  versions that are built, activated, served and listed - with a restart loading a version rather
  than reading the corpus again.
- Evidence manifests and citations that carry the source's content hash, the index version and
  the ledger position an answer reflects.
- The **provider plane**: declarations, credential references, probes and health, budgets
  (`rpm`, `tpm`, `dailyTokens`) enforced per tier with rolling minute and UTC-day windows, the
  max-tokens ceiling with its eight built-in ranges, and the **relay**
  (`POST /v1/providers/{name}/complete` and `/embed`) behind the same access gate as ingestion.
- **Sessions and runbooks**: the turn runner, applying a runbook as YAML kept one row per version,
  and the advisory path.

### Wire

- One contract, `openapi/munarium.v1.yaml`, is the source of both transports: JSON over HTTP and
  a generated gRPC surface, with the gRPC-only operations refused by name rather than approximated.
- **53 of the original's 121 operations** are served, measured by `tools/spec-coverage.ps1` and
  held to a floor in `UpstreamContractTests`, so the number cannot quietly regress.

### Build and quality

- .NET 11 (RC) and C# 15: runtime async, unions where an outcome is a closed set, nullable
  reference types, and central package management.
- The .NET analyzers and `SonarAnalyzer.CSharp` on every build with warnings as errors in
  shipping code, plus a SonarCloud job in CI.
- **956 tests** across five suites, a **NativeAOT smoke test** published and run on linux-x64,
  win-x64 and osx-arm64, and a **packaging gate** (`tools/release-packages.ps1`) that reads the
  packages back before a release may push them.

### Not in this release

Named here so a reader is not left to infer it, each with its reason in
[docs/decisions.md](docs/decisions.md) and its state in the README:

- Invocation-provenance events (`POST /v1/versions/{version_id}/events`), which is why a relayed
  call refuses `version_id`: refusing is better than making the call unrecorded.
- An embedding cache, which is why `cache_hit` is always false on the relay.
- The reports plane (`/v1/reports/*`), collections, runs, and ingest bulk.
- `index-build-jobs`, and the management-plane operations `/readyz` and `/openapi.json`.
- `index-artifacts` and `retrieval-rollout`: **absent by decision**, not pending.
