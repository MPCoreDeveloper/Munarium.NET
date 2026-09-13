
<div align="center">
  <video src="https://github.com/user-attachments/assets/516b69ac-624e-4af6-82bd-3001e9d8fbd2" controls muted playsinline></video>
</div>

# Munarium.NET

**Governed memory and traceable evidence for AI applications — the C# port.**

Munarium.NET re-implements **Munarium** on the .NET 11 / C# 15 toolchain. It gives an AI
application a memory it can be held to: an append-only fact ledger with governance in the write
path, retrieval that returns evidence rather than bare similarity, and model providers you bring
your own key for.

A model that answers out of a vector index can sound right and still be unverifiable. Munarium.NET
is built the other way round — every answer can be traced back to the exact document or record it
came from, carrying that source's content hash, the index version, and the ledger position it
reflects. A correction is a new fact with a supersession chain rather than a silent overwrite, and
a claim a policy blocks is recorded as disputed instead of being dropped.

## Credits and attribution

Munarium — its design, architecture, the Munarium Memory Protocol (MMP), the invariants, the
conformance suites and the documentation — is the original work of **Tyler Jensen**
([@tylerje](https://github.com/tylerje), tyler@tsjensen.com), published by **Ioka LLC** at
**[github.com/iokaio/munarium](https://github.com/iokaio/munarium)** under the Apache License 2.0.

Munarium.NET is an independent C# port of that work and gratefully builds on Tyler's original
design. It is not affiliated with, endorsed by, or maintained by Ioka LLC, and it does not track
the upstream repository. Full credit is recorded permanently in [CREDITS.md](CREDITS.md) and
[NOTICE](NOTICE); "Munarium" and "Ioka" are trademarks of Ioka LLC.

## Built on the MPCoreDeveloper stack

Munarium.NET is dogfooded end to end on the author's own .NET 11 libraries:

| Component | Role |
|---|---|
| [**SharpCoreDB**](https://github.com/MPCoreDeveloper/SharpCoreDB) | embedded, encrypted storage — vector search, full-text search and GraphRAG |
| [**SharpCoreDB.EventSourcing**](https://github.com/MPCoreDeveloper/SharpCoreDB) | the append-only ledger, including optimistic-concurrency conditional appends |
| [**SharpDispatch**](https://github.com/MPCoreDeveloper/SharpDispatch) | the zero-allocation CQRS command path |
| [**SharpPortico**](https://github.com/MPCoreDeveloper/SharpPortico) | OpenAPI → gRPC generation and the gRPC↔REST proxy |
| [**Posseth.UlidFactory**](https://github.com/MPCoreDeveloper/posseth.global.ulid) | spec-compliant, sortable ULID identifiers |

## How it is built

- **.NET 11 (RC) and C# 15** throughout, with nullable reference types and implicit usings enabled.
- **Runtime async** — the .NET 11 feature switch is on, so async methods compile to runtime-provided
  async instead of a compiler-generated state machine.
- **C# 15 unions** where an outcome is genuinely one of a closed set: the append result is
  `readonly union AppendOutcome(Appended, VersionConflict)`, so a caller that forgets to handle a
  version conflict does not compile. This is also what pins the port to .NET 11: the union feature needs
  `System.Runtime.CompilerServices.IUnion` and `UnionAttribute`, which only .NET 11's BCL provides, so a
  `net10.0` target refuses those declarations with `CS0518` and `CS0656` (and the runtime-async switch
  this project turns on needs `AsyncHelpers`, which .NET 10 carries as an evaluation-preview surface,
  `SYSLIB5007`). Multi-targeting net10 would therefore mean replacing those unions, not changing a
  property.
- **NativeAOT-compatible and platform-independent** — the libraries declare AOT/trim compatibility,
  so reflection and codegen hazards fail the build, and CI publishes and runs a fully AOT-compiled
  binary on `linux-x64`, `win-x64` and `osx-arm64`. One boundary is deliberate and written down under
  [Known limitations](#known-limitations).
- The .NET analyzers and `SonarAnalyzer.CSharp` run on every build; **warnings are errors** in
  shipping code, and code style is enforced during the build.
- Package versions live in one place (Central Package Management), and the whole stack is dogfooded
  rather than merely described.

## What is in the kernel so far

`src/Munarium.Core` holds the seams and the kernel types; the adapters live beside it.

| Piece | What it does |
| --- | --- |
| **Ledger** | `IStorageBackend`: a head read, an optimistic-concurrency append, a stream read and a global read. |
| **Governance** | The write path judges before it writes. A gate returns `Permitted` or `Blocked`, and a blocked claim is recorded as *disputed* rather than dropped — a refusal that cannot be recorded is not governance. Two gates ship: `shape` (a body that does not satisfy its shape) and `ledger-conflict` (a claim that would overwrite what its lineage already holds without saying so). A claim says what it is doing through `claim_type`: an *update* or a *correction* supersedes on purpose, an unnamed claim does not. |
| **Shapes** | Versioned, declarative shapes: a JSON Schema for the fact body, and the identity fields that decide supersession. A schema violation is a verdict, so the claim lands in the ledger with its reason attached. Validation is a documented, deterministic subset of JSON Schema implemented in the kernel — no parser library and no reflection, so the errors are stable enough to hash. |
| **Facts and pins** | Canonical fact encoding, supersession along a lineage, and an `as_of` pin that rebuilds the same slice — and the same SHA-256 digest — every time. A fact carries the version it was written to and the body it was claimed with, so a slice can be read back into the claims it came from and one version can be read out of the whole. |
| **Versions and lineage** | A version is an ordinary claim under the `version` shape, so it is judged by the same gates and rebuilt from the same slice as everything else — and its identity is immutable by construction, because claiming it twice is a ledger conflict. `GetLineage` walks parent links root-first, and `as_of_date` resolves to a pin through the version's own metadata. |
| **Context** | `ComposeContext` composes what a model would be given: the accepted facts of one version or shape, within a token budget, with refused claims listed separately under *Disputed*. It is a pure function of the pin, so the same pin composes the same text and the same `content_hash` — which is what makes the hash a cache key rather than a guess. |
| **Retrieval** | A retrieval seam that returns a `ProvenanceEnvelope` rather than bare similarity, and reciprocal rank fusion for combining a vector leg with a lexical one. |
| **Providers** | The model-provider seam, and a deterministic in-process embedding provider for tests and smoke runs. |
| **Wire** | One OpenAPI specification as the contract, one transport-agnostic operation surface behind it, and both surfaces served from it: JSON/HTTP by `Munarium.Server`, and gRPC/protobuf by the service base SharpPortico generates from that same specification - so the two cannot drift on names, shapes or enum values. |

`src/Munarium.Store.SharpCoreDb` is the storage and retrieval adapter over SharpCoreDB, and
`src/Munarium.Providers` holds the providers. `src/Munarium.Server` serves the wire surface over both
HTTP/JSON and gRPC, and its tests drive the real application - a real kernel, a real database, the
contract's own shapes, and the generated gRPC client - rather than a stand-in.

## Known limitations

Each of these is an upstream finding with the evidence that produced it, not a preference.

- **A NativeAOT server needs an AOT-safe store.** Publishing `Munarium.Server` as NativeAOT fails
  inside SharpCoreDB's persistence layer: `Database.Load`, `Database.SaveMetadata`,
  `SingleFileTable.FlushCache` and `UserService.LoadUsersInternal` serialise with reflection-based
  `JsonSerializer`, which ILC reports as `IL2026`/`IL3050`. The kernel, the wire contract and the
  event-sourcing storage path are clean, which is what the AOT smoke proves on all three platforms; a
  `JsonSerializerContext` inside SharpCoreDB is what would close the gap.
- **The gRPC surface is served, but is not itself an AOT target.** grpc-dotnet's binder looks up the
  binder method and each handler by reflection at startup (`BinderServiceModelFinder`,
  `ProviderServiceBinder`), so an ASP.NET Core gRPC server is not AOT-compilable. The messages, the
  contract and the generated client are reflection-free. Making this host AOT-clean needs the method
  discovery to be source-generated, which is grpc-dotnet's to do.
- **OpenAPI 3.1 is parsed as 3.0.** SharpPortico 1.1.0 accepts a 3.1 document by declaring it 3.0
  before parsing - its bundled parser (`Microsoft.OpenApi` 1.6.x) refuses 3.1 outright - and reports
  `SP1002` to say so. This contract stays at 3.0.3 anyway: it needs nothing from 3.1, and 3.0.3 is what
  every tool reads.
- **A version has to be one URL path segment.** Version identities travel in a path
  (`/v1/versions/{version_id}/claims`), so an identity containing `/` cannot be addressed. Either
  identities stay segment-safe or the parameter moves to a query string.

## What is not ported yet

The kernel is finished first, because everything else is a thin adapter over it. What is missing, roughly
in the order it is planned:

- **The rest of the gate vocabulary.** The original's gates are `anchor-consistency`, `ledger-conflict`,
  `orphaned-reference`, `meta-leakage`, `lexical-similarity` and the `chronology-*` family. `shape` and
  `ledger-conflict` are here. The others judge concepts this port does not have yet — anchors,
  sources and references, promises with deadlines — so they arrive with those rather than before them.
- **Ingestion and index versions.** `/v1/search` answers with a real provenance envelope, but there is no
  `/v1/ingests` or `/v1/indexes` yet, so an index is what a host put in it rather than a versioned
  artefact the ledger knows about. Index version → envelope → ledger watermark is the demonstration that
  closes this.
- **Idempotency keys.** The original requires an `idempotency-key` metadata entry on every command RPC and
  replays the stored result. Here a retry writes a second claim; the `ledger-conflict` gate treats a
  re-sent claim as a retry only when nothing about it changed, which is an approximation and is written
  down as one.
- **Pagination** (`PageRequest`/`PageResponse`), **authentication and tenancy**, **sessions and runbooks**,
  **anchors, promises, counters and the digest ladder**, and the separate **`matrix/v1` semantic query**
  surface. The original carries roughly 49 RPCs across 8 services; the kernel's core is what is served
  here.
- **Clients for other languages.** The gRPC contract is language-neutral, but this port is .NET-first: the
  .NET surface is what is built and tested against the contract, and clients for other languages are a
  later consideration rather than a commitment.

## Status

Early days: this repository is the C# port in progress, and the kernel (`src/Munarium.Core`) is the
first piece of it. The design it follows — and the executable specification it will be held to —
is described and proven in the [original project](https://github.com/iokaio/munarium).

## License

Apache-2.0, the same license as the original Munarium. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
