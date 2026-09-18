
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
| [**Posseth.UlidFactory**](https://github.com/MPCoreDeveloper/posseth.global.ulid) | spec-compliant, sortable ULID identifiers — and the ledger's identities, which is how the kernel knows *when* something was written without ever reading a clock |

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
| **Governance** | The write path judges before it writes. A gate returns `Permitted` or `Blocked`, and a blocked claim is recorded as *disputed* rather than dropped — a refusal that cannot be recorded is not governance. The command path ships two gates: `shape` (a body that does not satisfy its shape) and `ledger-conflict` (a claim that would overwrite what its lineage already holds without saying so); a claim says what it is doing through `claim_type`, so an *update* or a *correction* supersedes on purpose and an unnamed claim does not. |
| **The gate vocabulary** | All six rule families, as pure functions of a pinned snapshot and a candidate unit: `anchor-consistency`, `ledger-conflict`, `orphaned-reference`, `meta-leakage` and `lexical-similarity` always run together (with the anchor finding subsuming the conflict for the same detail), and the `chronology-*` family — order, containment, overlap, deadlines, durations — runs when a deployment declares rules. A withheld claim is reported as a finding carrying its rule id, severity and the claim key the accept path disputes, and a write's findings are recorded in the same append as the claims it judged, so a verdict and the write it belongs to cannot drift apart. Only the newest candidate-side contributor is named, so a verdict never blames the ledger's own history. |
| **Sources** | A document's identity is its logical path and not its content hash, so the same bytes at two paths are two sources and a path prefix can bind a collection; the hash stays as integrity, recorded and surfaced in provenance. Constructing a key validates the path (byte-bounded, no traversal, no absoluteness, no Windows drive, no empty or dotted segment) and document ingress refuses the reserved `evidence/` keyspace outright - as a separate predicate, so the evidence writer that legitimately builds keys there has no check to bypass. |
| **Identities** | The ledger's identities are ULIDs, so one value is three things: what a claim *is*, *when* it happened (a 48-bit millisecond count inside the first ten characters), and *how it sorts* against its neighbours (Crockford Base32 preserves order, so ordinal comparison is time comparison). The kernel therefore never reads a clock — `MeshSnapshot.WrittenAt` is derived from the identities the snapshot holds — which is what makes a chronology finding reproducible by anyone who holds the snapshot. The order an identity carries is a *time* order and deliberately not the ledger's: the pin is a position the store assigned, because two writers whose clocks disagree would otherwise resolve the same fact differently. So `ClaimResolution` keeps reading sequences, and the identity order is what a caller uses when it has identities but no positions. |
| **Claims and resolution** | The semantic model the gates reason over: `subject.key=value` with the scope it was written in, its provenance, and the claim it supersedes. `ClaimResolution` is the reference implementation of the ledger's read semantics — the superseded set is itself filtered by the pin, so a claim superseded only *after* the pin still reads as current at the pin. Every storage backend's query has to agree with it. |
| **Anchors, promises and counters** | A locked detail may not drift (`anchor-consistency`); a promise made in one scope is owed to a later one, and one fulfilled after the pin reads back *open*; a counter is a whole-document frequency with an optional ceiling, and the writer is told what is left rather than only what it overspent. |
| **Digest ladder** | Deterministic, model-free compression rungs: tier 0 per scope, tier 1 per scope-prefix group with the values elided, tier 2 the whole-lineage rollup. Rungs are *rebuilt* from the pinned facts rather than served, because stored digest text has no history to read at a pin. |
| **Chronology** | A closed calendar grammar (ISO dates, `YYYY-MM`, `YYYY`, month names, ranges, seasons, and `circa`/`approx`/`~` hedges) and the certainty algebra on top of it: `DefinitelyBefore` is true only when the comparison is certain given both precisions and both hedges, so an intentionally approximate date is never a violation by itself. |
| **Shapes** | Versioned, declarative shapes: a JSON Schema for the fact body, and the identity fields that decide supersession. A schema violation is a verdict, so the claim lands in the ledger with its reason attached. Validation is a documented, deterministic subset of JSON Schema implemented in the kernel — no parser library and no reflection, so the errors are stable enough to hash. |
| **Facts and pins** | Canonical fact encoding, supersession along a lineage, and an `as_of` pin that rebuilds the same slice — and the same SHA-256 digest — every time. A fact carries the version it was written to and the body it was claimed with, so a slice can be read back into the claims it came from and one version can be read out of the whole. |
| **Versions and lineage** | A version is an ordinary claim under the `version` shape, so it is judged by the same gates and rebuilt from the same slice as everything else — and its identity is immutable by construction, because claiming it twice is a ledger conflict. `GetLineage` walks parent links root-first, and `as_of_date` resolves to a pin through the version's own metadata. |
| **Context** | `ComposeContext` composes what a model would be given: the accepted facts of one version or shape, within a token budget, with refused claims listed separately under *Disputed*. It is a pure function of the pin, so the same pin composes the same text and the same `content_hash` — which is what makes the hash a cache key rather than a guess. |
| **Retrieval** | A retrieval seam that returns a `ProvenanceEnvelope` rather than bare similarity. The adapter over SharpCoreDB runs two independent legs — Okapi BM25 through the engine's own analyzing tokenizer, stemmer and stop words, and a vector leg that is either an exact scan or a DiskANN Vamana graph — and fuses them **by rank**, because a BM25 score and a cosine distance do not share a scale. Fusion is the port's own so the answer and its envelope are produced together: the ranking that decided the answer is the ranking the envelope records, and opaque chunk ids stay opaque where the engine's fusion parses them back to numbers. |
| **Index versions** | An index version is an immutable snapshot of one collection's corpus as one shape sees it, and its identity is a hash of everything that determines what a query would match — collection, shape, engine, chunker, extractors, embedder and the source/hash bindings. So a rebuild of one corpus is one version and any change that alters the text or the vectors is another, which makes a build idempotent and a cutover meaningful. Segments are joined with a unit separator and the sources are sorted and deduped before hashing: the same bug the evidence plane's domain key fixed, where material joined with a printable character can be reached two ways. Building records a version **without** making it live, a cutover is per collection and atomic, a superseded version stays resolvable, and `ResolveAsync` takes an answer's envelope and checks that the version it names exists, that the bytes it cites are ones that version indexed, and that it does not claim more of the ledger than the index ever reflected. |
| **Evidence** | A manifest is a promise about bytes: the contract version and canonicalization, the artifact and logical-result hashes, the schema, the row identity rule, the snapshot vector, and the authorization class. The domain key excludes the artifact hash — re-serializing one logical result must not mint a second artifact — and includes the authorization class, joined with a unit separator, so one compartment holding a comma cannot be two compartments. Content is verified canonically (`sha256:` plus lowercase hex), and a check reports the two lengths and the two hashes rather than a bare yes: an operator chasing a mismatch needs to know what was expected and what arrived. |
| **The evidence hierarchy** | A research profile resolves into a plan of layers, each with pinned sources, a requirement (`required`, `optional`, `fallback`), a role and its own context budget. `HierarchyRunner` runs them in trust order: providers are tried in order and the first that claims a source wins, a fallback layer runs only when nothing before it produced evidence, and a plane-qualified source (`matrix:`, `facts:`) that no provider claims **refuses** rather than quietly becoming a document search that reports a required layer satisfied. A required layer that refused stops the turn — as a result rather than an exception, because a refusal is something the answer has to disclose. `HierarchyComposer` then composes the blocks: highest trust occupies the budget first, a `preserve_complete_result` layer is taken whole or dropped, every block is labelled `COMPLETE` or `TRUNCATED`, and rows are numbered by one function the served-evidence list shares, because a checker that numbered rows differently from the text the model read would reject correct citations. |
| **Providers** | The model-provider seam, and a deterministic in-process embedding provider for tests and smoke runs. |
| **Wire** | One OpenAPI specification as the contract, one transport-agnostic operation surface behind it, and both surfaces served from it: JSON/HTTP by `Munarium.Server`, and gRPC/protobuf by the service base SharpPortico generates from that same specification - so the two cannot drift on names, shapes or enum values. A new operation is one specification entry plus one adapter per surface; the generated client is what the gRPC tests drive, which is why the contract file is the only place an operation is declared. |

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
- **OpenAPI 3.1 is parsed as 3.0.** SharpPortico 1.1.1 accepts a 3.1 document by declaring it 3.0
  before parsing - its bundled parser (`Microsoft.OpenApi` 1.6.x) refuses 3.1 outright - and reports
  `SP1002` to say so. This contract stays at 3.0.3 anyway: it needs nothing from 3.1, and 3.0.3 is what
  every tool reads.
- **A version has to be one URL path segment.** Version identities travel in a path
  (`/v1/versions/{version_id}/claims`), so an identity containing `/` cannot be addressed. Either
  identities stay segment-safe or the parameter moves to a query string.

## What is not ported yet

The kernel is finished first, because everything else is a thin adapter over it. What is missing, roughly
in the order it is planned:

- **The candidate plane is on the write path and on every wire surface, including the findings read.** The
  `CandidateLedger` judges a whole unit against the head it was judged against, records a blocked claim as
  disputed, and lands the claims and the write's findings in **one** conditional append - so the response and the
  record cannot disagree, where the original writes findings to a separate table and treats a failure as a
  warning. `POST /v1/versions/{id}/claim-batches` (and the `ProposeClaimBatch` RPC) carries the batch, returns
  each claim's verdict with the findings that produced them, and answers a caller's `expected_head` pin with a
  contention rather than a re-gate; `GET /v1/versions/{id}/findings` (and `ListFindings`) reads them back with
  the original's query (pin, severity, exact rule, rule prefix, limit). One thing to know when reading both: a
  finding is stamped with the position its write settled at **inside its version's stream**, while a facts read
  answers on the **global** feed its pin is on - the two are related through the version they belong to, not
  directly comparable.
- **The evidence hierarchy runs in the kernel, but nothing serves it yet.** A plan executes in trust order, a
  plane-qualified source refuses when nothing is bound to it, a required layer's refusal stops the turn, and
  composition honours both the profile budget and the whole-or-nothing rule — all against test doubles. What is
  missing is the server side: providers bound to the real planes (a semantic data view, the fact ledger, sealed
  artifacts), an `IEvidenceStore` adapter, the research profiles a runbook declares, progress on the wire, and
  the per-turn hierarchy decision persisted where an operator can read it.
- **Index versions are built and persisted; nothing on the wire triggers a build yet.** The catalogue, the derived
  identity, the cutover rules and the envelope resolution are in the kernel and tested, the manifests are stored in a
  table of their own (retrieval bookkeeping is deliberately not ledger data), and `IndexBuilder` reads the sources a
  collection binds, cuts and embeds them into a **new** instance nobody is answering from - so a rebuild happens while
  the live version keeps serving, and the host keeps the built instance so activating it is a cutover rather than a
  second build. A bound document this port cannot read refuses the build by name, and the half-built instance is
  dropped, so a corpus cannot be activated with the documents the build got to before it stopped. What is missing is
  the surface: there is no `/v1/indexes` route to build, activate or read a version, and provenance resolution is a
  kernel operation rather than a route.
- **Ingestion is on the wire; rebuilding an index is not.** `PUT /v1/sources` stores a document's bytes, records the
  source row, cuts the text into `chunk@1` chunks, embeds them and writes them into the index version `/v1/search`
  answers from; `GET /v1/sources/{source_id}` answers where the bytes went, never the bytes. A re-put of the same bytes
  writes nothing and indexes nothing, a changed document at one path is one source replaced, and the citation an answer
  carries resolves to the path and the hash it was stored under. Two limits are stated rather than hidden: the contract
  carries a document's **text**, because this port reads text - a PDF or a DOCX is refused by name, since the upstream
  extractors depend on a model capability this port has not ported - and the ingest writes into whichever version is
  serving, which is read at the moment of use so a cutover cannot split one document between two versions.
- **Three of the four keyed planes are authorable; entities and ingest are not.** Every plane a snapshot carries is
  served: `GET /v1/snapshots` answers with one pin across all of them - facts resolved-current, the digest ladder
  rebuilt from them, the scope filter and fact limit applied after resolution, and the keyed planes as they stood at
  that position, plus the instant derived from the snapshot's own identities rather than from a clock. Anchors,
  promises and counters have their own routes: `POST/GET /v1/versions/{id}/anchors` with
  `POST .../anchors/{detail_key}/release`, `POST/GET /v1/versions/{id}/promises` - the promise check's overdue
  findings computed over the full pinned slice before any filter narrows it - with
  `POST .../promises/{key}/fulfill`, and `POST/GET /v1/versions/{id}/counters`, where the read answers with the
  directives a writer would be given rather than only the totals. What is missing: nothing on the wire records an
  entity (upstream resolves entities through a model capability this port has not ported, so writing them would be
  raw plumbing), and no route rebuilds an index version from the rows a deployment already has.
- **Idempotency keys.** The original requires an `idempotency-key` metadata entry on every command RPC and
  replays the stored result. Here a retry writes a second claim; the `ledger-conflict` gate treats a
  re-sent claim as a retry only when nothing about it changed, which is an approximation and is written
  down as one.
- **Pagination** (`PageRequest`/`PageResponse`), **authentication and tenancy**, **sessions and runbooks**,
  and the separate **`matrix/v1` semantic query** surface. The original carries roughly 49 RPCs across 8
  services; the kernel's core is what is served here.
- **Clients for other languages.** The gRPC contract is language-neutral, but this port is .NET-first: the
  .NET surface is what is built and tested against the contract, and clients for other languages are a
  later consideration rather than a commitment.

## Status

Early days: this repository is the C# port in progress, and the kernel (`src/Munarium.Core`) is the
first piece of it. The design it follows — and the executable specification it will be held to —
is described and proven in the [original project](https://github.com/iokaio/munarium).

## License

Apache-2.0, the same license as the original Munarium. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
