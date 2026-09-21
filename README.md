
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
- **A release is packed and read back before it is pushed** — the set of packages, and the licence,
  notice, credits, readme, icon and symbols inside each one, are checked by
  [tools/release-packages.ps1](tools/release-packages.ps1) on every push and again in the release
  workflow: [docs/release.md](docs/release.md).
- **The original is the specification, and a claim about it gets read before it gets written** — four
  "capabilities this port lacks" turned out to be unmeasured assumptions, and each is on record with
  what the source actually says: [docs/method.md](docs/method.md).

## What is in the kernel so far

`src/Munarium.Core` holds the seams and the kernel types; the adapters live beside it.

The table below is the summary of what is in the kernel so far. The detail behind each piece lives in `docs/`, one
document per area, and every row links to the section it summarises - so a piece's design can be read, and corrected,
without touching a table cell on the far side of a screen.

| Piece | What it does |
| --- | --- |
| **Ledger** | `IStorageBackend`: a head read, an optimistic-concurrency append, a stream read and a global read. [More](docs/ledger.md#ledger) |
| **Governance** | The write path judges before it writes. A gate returns `Permitted` or `Blocked`, and a blocked claim is recorded as *disputed* rather than dropped — a refusal that cannot be recorded is not governance. The command path ships two gates: `sha... [More](docs/ledger.md#governance) |
| **The gate vocabulary** | All six rule families, as pure functions of a pinned snapshot and a candidate unit: `anchor-consistency`, `ledger-conflict`, `orphaned-reference`, `meta-leakage` and `lexical-similarity` always run together (with the anchor finding subsumin... [More](docs/ledger.md#the-gate-vocabulary) |
| **Sources** | A document's identity is its logical path and not its content hash, so the same bytes at two paths are two sources and a path prefix can bind a collection; the hash stays as integrity, recorded and surfaced in provenance. [More](docs/sources-and-retrieval.md#sources) |
| **Identities** | The ledger's identities are ULIDs, so one value is three things: what a claim *is*, *when* it happened (a 48-bit millisecond count inside the first ten characters), and *how it sorts* against its neighbours (Crockford Base32 preserves order... [More](docs/ledger.md#identities) |
| **Claims and resolution** | The semantic model the gates reason over: `subject.key=value` with the scope it was written in, its provenance, and the claim it supersedes. [More](docs/ledger.md#claims-and-resolution) |
| **Anchors, promises and counters** | A locked detail may not drift (`anchor-consistency`); a promise made in one scope is owed to a later one, and one fulfilled after the pin reads back *open*; a counter is a whole-document frequency with an optional ceiling, and the writer is... [More](docs/ledger.md#anchors-promises-and-counters) |
| **Digest ladder** | Deterministic, model-free compression rungs: tier 0 per scope, tier 1 per scope-prefix group with the values elided, tier 2 the whole-lineage rollup. [More](docs/ledger.md#digest-ladder) |
| **Chronology** | A closed calendar grammar (ISO dates, `YYYY-MM`, `YYYY`, month names, ranges, seasons, and `circa`/`approx`/`~` hedges) and the certainty algebra on top of it: `DefinitelyBefore` is true only when the comparison is certain given both precis... [More](docs/ledger.md#chronology) |
| **Shapes** | Versioned, declarative shapes: a JSON Schema for the fact body, and the identity fields that decide supersession. [More](docs/ledger.md#shapes) |
| **Facts and pins** | Canonical fact encoding, supersession along a lineage, and an `as_of` pin that rebuilds the same slice — and the same SHA-256 digest — every time. [More](docs/ledger.md#facts-and-pins) |
| **Versions and lineage** | A version is an ordinary claim under the `version` shape, so it is judged by the same gates and rebuilt from the same slice as everything else — and its identity is immutable by construction, because claiming it twice is a ledger conflict. [More](docs/ledger.md#versions-and-lineage) |
| **Context** | `ComposeContext` composes what a model would be given: the accepted facts of one version or shape, within a token budget, with refused claims listed separately under *Disputed*. [More](docs/ledger.md#context) |
| **Retrieval** | A retrieval seam that returns a `ProvenanceEnvelope` rather than bare similarity. [More](docs/sources-and-retrieval.md#retrieval) |
| **Index versions** | An index version is an immutable snapshot of one collection's corpus as one shape sees it, and its identity is a hash of everything that determines what a query would match — collection, shape, engine, chunker, extractors, embedder and the ... [More](docs/sources-and-retrieval.md#index-versions) |
| **Evidence** | A manifest is a promise about bytes: the contract version and canonicalization, the artifact and logical-result hashes, the schema, the row identity rule, the snapshot vector, and the authorization class. [More](docs/evidence.md#evidence) |
| **The evidence hierarchy** | A research profile resolves into a plan of layers, each with pinned sources, a requirement (`required`, `optional`, `fallback`), a role and its own context budget. [More](docs/evidence.md#the-evidence-hierarchy) |
| **Turns** | `TurnPipeline` is the kernel's half of a turn, and it resolves nothing: the plan, the template, the model and the budget all arrive already chosen, because which model, which keys and which tenant are a deployment's business. [More](docs/sessions-and-runbooks.md#turns) |
| **Sessions** | A session is a conversation with a memory of what it may see. [More](docs/sessions-and-runbooks.md#sessions) |
| **Applied runbooks** | A runbook is applied as YAML and kept as one row per version, because the reference is `name@version`: a session pins one, and a pin whose own document could be outlived by a newer one would not be a pin. [More](docs/sessions-and-runbooks.md#applied-runbooks) |
| **Providers** | The model-provider seam, with a deterministic in-process embedding provider for tests and smoke runs; the provider plane, which keeps applied declarations (a dialect, an endpoint, the models it serves, where the credential lives — never the credential — and the budgets it declares), probes them per family and tier, and relays a completion behind the access gate under the deployment's own ceiling; and the evidence hierarchy's two real planes. [More](docs/providers-and-wire.md#providers) |
| **Wire** | One OpenAPI specification as the contract, one transport-agnostic operation surface behind it, and both surfaces served from it: JSON/HTTP by `Munarium.Server`, and gRPC/protobuf by the service base SharpPortico generates from that same spe... [More](docs/providers-and-wire.md#wire) |

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
- **The evidence hierarchy runs in the kernel, and the artifacts behind it are served.** A plan executes in trust order, a
  plane-qualified source refuses when nothing is bound to it, a required layer's refusal stops the turn, and
  composition honours both the profile budget and the whole-or-nothing rule — all against test doubles. Behind it, the
  artifact path is real: `POST /v1/evidence` seals a manifest inline or hands back a single-use grant, `PUT
  .../bytes` and `POST .../commit` finish that flow, `GET /v1/evidence/{id}` resolves a citation to its manifest,
  `GET .../rows` reads a bounded window in the sealed order, `GET .../accesses` reports who resolved it, and
  `DELETE .../{id}` with `POST .../legal-hold` is the operator's side — all of it in the original's words, statuses and
  reasons, kept in tables of their own. Both transports serve it: the gRPC adapters are hand-mapped field by field, and
  the row read is refused by name there (`Unimplemented`, with the reason) because a row keyed by the column names a
  manifest declares has no faithful protobuf form — the JSON surface keeps an empty value and an absent value apart, and
  a protobuf map cannot. Two of the three planes behind the hierarchy are bound too: the ledger's own facts and a
  semantic data view over REST, with a circuit breaker, refusals instead of errors, and a parser tested against the
  contract's own examples. The profiles those planes are declared in are ported as well — the declarations, their
  validation, and the resolution of a profile into a plan. The runbook a deployment applies is readable too: the YAML is
  mapped field by field onto the document (sources, collections with their bindings, retrieval and fusion, data views
  with their typed parameters, model defaults per task level, completion and verification, execution order and steps),
  and the rules that would otherwise fire mid-turn are refused at **load** — a document with no steps, one that declares
  both a shape and collections or neither, a semantic data view with no `intent` task, a `cutover.approval` typo, a
  `keep_versions` above the ceiling, and any profile the research rules reject. The original's own worked example is the
  conformance fixture, byte for byte. The semantic layer a document is then judged on is reported as **findings** rather
  than refusals — step ordering, collection names and levels, source prefixes, every retrieval range, the model tiers
  and task levels, the completion's budget and placeholders — each with a severity, a stable dotted code, a message and a
  YAML path, and the original's own worked example validates clean. Applied runbooks are kept, and resolving one gives
  back the document a turn reads — with the two-pass removal that never deletes, and the rule that a removed version is
  refused rather than resurrected. What is still missing: sealed artifacts as a source and the session and turn
  operations on the wire — the turn runs end to end (intent, evidence, one answer, deterministic checks, bounded
  correction, and the decision recorded on the turn rather than inside the capped audit), a session and its turns are
  recorded with the ordinal allocated by the store, the runbook a session pins can be applied and resolved by name, and
  the models a turn uses resolve through the runbook's task levels — and applying and listing a runbook are served on both
  transports, which is the pattern the rest of the session plane then followed: applying and listing a runbook, the
  session and turn operations (`create`, `turn`, `get`, `close`), and the turn's progress stream as SSE are all served -
  the stream is JSON-only, and `StreamTurn` refuses by name over gRPC exactly as the row read does, because the
  original's protobuf carries no streaming method for sessions and a unary answer would look like a turn that never
  reported a stage.
- **Index versions are built, served and listed; the index itself lives in the process that built it.** The catalogue,
  the derived identity, the cutover rules and the envelope resolution are in the kernel and tested, the manifests are stored in a
  table of their own (retrieval bookkeeping is deliberately not ledger data), and `IndexBuilder` reads the sources a
  collection binds, cuts and embeds them into a **new** instance nobody is answering from - so a rebuild happens while
  the live version keeps serving, and the host keeps the built instance so activating it is a cutover rather than a
  second build. A bound document this port cannot read refuses the build by name, and the half-built instance is
  dropped, so a corpus cannot be activated with the documents the build got to before it stopped. The surface is
  there too: `POST /v1/indexes` builds, `POST /v1/indexes/{id}/activate` cuts a collection over (refusing a version
  this process never built, because it cannot answer from chunks it does not have), `GET /v1/indexes/active` and
  `GET /v1/indexes/{id}` read the state, `GET /v1/indexes?collection_id=` lists a collection's versions - superseded
  ones included, since cutting back to one is a cutover rather than a restore - and `POST /v1/indexes/resolve` takes an
  answer's envelope and says whether the bytes it cites were in the version it names. What a restart does with that is
  measured rather than assumed: the chunks of a version are persisted - one table per version, named from a digest of it
  - so a deployment that comes back **loads** a version instead of reading the corpus again, and the recovery reports
  which corpus could not come back rather than answering that it is empty. `IndexBuilder.LoadAsync` hands back the
  served instance straight from the persisted rows, and only the lexical leg is rebuilt from the persisted chunk text,
  because the engine's `FullTextIndex` is an in-process class and no persisted full-text index was found (measured). A
  version whose chunks were never persisted is built again instead, which is what a deployment does once. Verification
  follows the original's stance rather than its shape: `IndexArtifactVerification` opens the persisted rows and checks
  them against the manifest - every vector has the width the manifest records, every chunk obeys the maximum the build
  cut to, every source the manifest names is represented and nothing else is, and a document's ordinals are contiguous,
  because a gap is the one failure a count cannot see.

The route the engine offers was measured too, and is not taken yet: SharpCoreDB has a native `Vector` column type, a
`CREATE VECTOR INDEX ... USING FLAT|HNSW|DISKANN` whose definition the engine keeps in the table's own metadata, and a
vector index that persists under its `vector_index:` storage prefix. This port writes each embedding into that native
column and reads it back, and the table is declared per version - but the search still runs over the rows loaded into
the in-process index, because this port's fusion is its own (a BM25 score and a cosine distance do not share a scale) and
the envelope has to record the ranking that decided the answer. Adopting the engine's index is therefore a way to move
one leg rather than the corpus: it can be adopted without giving up this port's fusion, since the module supplies the
candidates and the envelope still records the ranking this port computed. Whether a fleet is in scope at all is a
deployment-shape decision, and it is recorded in docs/decisions.md together with what was measured for it.

The two-stage
  collection selection a wide runbook may ask for is in, and how it behaves is written here rather than left to be
  discovered: the ranking is in the kernel - `CollectionSelection`, with the original's own measured thresholds as its
  tests, so a pool that is 85% phrase counts 3.55× and one that is 6% counts 1.18× - and the probe it ranks runs per
  collection, because a probe searches each permitted collection separately: the store was already per collection
  (`IIndexVersionStore` reads a collection's live version and a cutover moves exactly one collection), and what was
  missing was the host and the fan-out, both of which are in - `IIndexHost.ReaderFor` hands back the reader of a version
  the deployment did not cut over to, `CollectionIndexes` resolves a collection's *name* to that reader through the live
  version, and a turn probes every permitted collection with the question as asked, deepens the strongest with the
  widened one, and merges the rest's probe pools anyway - selection spends the deep search rather than narrowing the
  answer. Two things about it are this port's rather than the original's and are said here because of that:
  the fan-out is sequential where the original bounds it by a concurrency setting, and a deployment with no per-collection
  version at all searches its one serving index for the whole turn, which is the state the original cannot be in and the
  behaviour every turn had before the seam existed. The other retrieval step a runbook may declare is executed too:
  `modelQueryExpansion` widens the question through the task level the runbook pins for it - one paid call at temperature
  zero, the prompt's constraints enforced by a parser that refuses a capitalised term rather than folding it down, the
  variants appended to the text the lexical leg reads while the embedding stays the question's own.
- **Ingestion is on the wire.** `PUT /v1/sources` stores a document's bytes, records the
  source row, cuts the text into `chunk@1` chunks, embeds them and writes them into the index version `/v1/search`
  answers from; `GET /v1/sources/{source_id}` answers where the bytes went, never the bytes, and `GET /v1/sources` lists
  what the deployment holds, optionally under one prefix - which is how an operator checks a collection's binding before
  building over it. A re-put of the same bytes
  writes nothing and indexes nothing, a changed document at one path is one source replaced, and the citation an answer
  carries resolves to the path and the hash it was stored under. A document travels as its **text** or as its **bytes**
  (base64, exactly one of the two), and both binaries upstream reads are read here: a DOCX out of its own XML, since a
  `.docx` is a zip the base class library opens, and a PDF's text layer through PdfPig - pure managed, so no native
  library, no rasterizer and no model are involved, and CI proves it by extracting from a PDF inside the NativeAOT binary
  it publishes per platform, so a binary document is retrievable like any other. OCR is the capability that is not
  ported, so a scan reads as **empty** - not a miss hidden but the signal that path keys on - and a PDF whose fonts carry
  no Unicode mapping yields those fonts' own codes, which no text-layer reader can turn into words. A further limit is
  that the ingest writes into whichever version is
  serving, which is read at the moment of use so a cutover cannot split one document between two versions.
- **Three of the four keyed planes are authorable, and the fourth is not authorable anywhere.** Every plane a snapshot carries is
  served: `GET /v1/snapshots` answers with one pin across all of them - facts resolved-current, the digest ladder
  rebuilt from them, the scope filter and fact limit applied after resolution, and the keyed planes as they stood at
  that position, plus the instant derived from the snapshot's own identities rather than from a clock. Anchors,
  promises and counters have their own routes: `POST/GET /v1/versions/{id}/anchors` with
  `POST .../anchors/{detail_key}/release`, `POST/GET /v1/versions/{id}/promises` - the promise check's overdue
  findings computed over the full pinned slice before any filter narrows it - with
  `POST .../promises/{key}/fulfill`, and `POST/GET /v1/versions/{id}/counters`, where the read answers with the
  directives a writer would be given rather than only the totals. What is missing is a writer for entities, and the
  honest version of that is not a gap: upstream declares `entities` on its snapshot and hard-codes it empty - its whole
  tree constructs no `Entity` at all, with no resolution step and no route - so there is nothing there to port. This port
  folds an `entity.resolved` event into the plane like every other one, which is tested.
  Nothing here writes one either: the writer this port lacks is one that would have to fill a plane the original never
  filled. One thing that looked missing is settled by how the build is written: it reads the rows a deployment
  already holds - the prefix's rows from the registry, then each one's bytes out of the store - rather than taking a
  window of freshly ingested documents, so `POST /v1/indexes` over a prefix somebody ingested last week *is* a rebuild,
  and it derives the same version, because a version's identity is a hash of everything the build would do.
- **The provider plane keeps declarations, probes them, and relays calls under a ceiling.** `POST /v1/providers` records a
  declaration - a dialect, an endpoint, the models that dialect serves, where the credential lives (an environment
  variable's name or a file's path, never the material) and the rate and daily-token budgets it is held to - and never
  the credential, which is the original's own line and the reason `GET /v1/providers` is free while `/healthai` spends
  tokens. The reserved name `default` is refused, three environment-backed `default-<family>` declarations stand behind
  whatever was applied without ever being stored, and each one reports the concrete model its fast, capable and frontier
  tiers resolve to - a tier override first, then the family's built-in table.
  <br>`POST /v1/providers/{name}/complete` is the relay, and it is a privilege: the deployment spends its own credential
  on a caller's behalf, so the gate is in front of the route rather than beside it, and `name` is the reserved `default`
  only when a caller names a family to reach for. The order is the design - the configuration resolves, then the model
  (an explicit one, the tier, the configuration's own first model, the family's capable built-in), then the ceiling, then
  the configuration's declared budget, and only then the call - and what comes back is not turn evidence and is recorded
  nowhere. A call naming a `version_id` is refused by name, because the invocation-provenance plane that would record it
  is not ported; over gRPC the same route answers, and `embed` is JSON-only there because an array of numbers inside an
  array of vectors has no faithful protobuf form. The port keeps no embedding cache, so `cache_hit` is reported false
  rather than invented.
  <br>`GET`/`POST /v1/max-tokens` is the ceiling every paid call is held to - one object of eight per-call output-token
  ceilings, the original's built-ins with the process's `MUNARIUM_MAX_TOKENS_*` variables over them and a tenant's
  replacement in front of both, read where the calls are made: a turn's answer, its query expansion and its intent
  classifier, a runbook's advisory pass, the guided-authoring assist, each provider probe and a relayed completion. A
  budget a configuration declares is enforced by the relay over a rolling minute and a UTC day per tier; that state lives
  in the process that enforces it, because this deployment is one node by decision.
- **Idempotency keys on every command that records.** A command can carry an `idempotency_key` (a ULID, the same shape
  this port mints everywhere else), and every route that writes honours it: claims and batches, locks and releases,
  promises and fulfilments, counters, and version creation. The second attempt writes nothing, is judged by nothing, and
  is answered with exactly what the first attempt was answered - because the key belongs to the command and not to the
  transport, a retry over the other surface is answered too. A key is scoped to the operation and what it names: the
  version, and the detail or promise where the command names one, so one caller's key for one write cannot swallow
  another's. Version creation is the exception that proves the rule - the version id may be minted *during* the command,
  so that scope is the key alone, which is what makes a retry land on the version that was created rather than on a second
  one. Only an answer that *recorded* something is remembered, so a contention, a refusal, or a release or fulfilment that
  changed nothing can still be reached again rather than be answered forever with a no-op; and the first answer is the
  answer, because two answers to one request is the situation the whole seam exists to prevent. A command that has a body
  carries the key in it; the release and fulfilment routes carry no body, so the key is a query parameter there - the one
  place the contract puts it outside the body, and for that reason.
- **Pagination** (`PageRequest`/`PageResponse`), and the management plane. This port threads a principal through
  every route and derives access from it, so the identity and tenancy *model* is in; what is not is issuing tokens,
  which is the management plane's rather than the data plane's. The separate **`matrix/v1` semantic query** surface
  belongs to another repository: this port is a client of it, with the parser held against the contract's own
  examples. The original's RPC count is not the goal here - what is missing is named, item by item, in this list
  rather than left to be inferred from a number.
- **Clients for other languages.** The gRPC contract is language-neutral, but this port is .NET-first: the
  .NET surface is what is built and tested against the contract, and clients for other languages are a
  later consideration rather than a commitment.

## Status

The port is deep rather than wide. The kernel (`src/Munarium.Core`), the SharpCoreDB adapter, the ingest, index and
session planes, the provider plane, and both transports - JSON/HTTP and gRPC, generated from one contract - are in and
tested: 957 tests across five suites, with `docs/` carrying the design behind each piece. What is not ported is listed
above, item by item: 53 of the original's 121 operations are served, measured by `tools/spec-coverage.ps1` and held to a
floor in `UpstreamContractTests`, and every operation that remains is either absent by decision (`docs/decisions.md`) or
the next slice in the order that document fixes - none of them a half-built feature. The design it follows - and the
executable specification it is held to - is described and proven in the
[original project](https://github.com/iokaio/munarium).

## Releases

Five libraries are published to NuGet, each with a symbol package and the licence, notice, credits, readme
and icon a consumer is entitled to: **`Munarium.Core`** (the kernel), **`Munarium.Wire`** (the contract and
its generated surface, with `openapi/munarium.v1.yaml` inside it), **`Munarium.Providers`**,
**`Munarium.Runbooks`** and **`Munarium.Store.SharpCoreDb`**. Each package carries a source link to the
commit it was built from, so a debugger steps into this repository rather than into a decompilation.

`Munarium.Server` is not a package: it is the host, and a deployment builds it from source so that its
transport, storage and provider wiring stay the deployment's decision. The libraries target `net11.0`, so
building against them needs the .NET 11 SDK.

The packages are preview, and the version is a tag: pushing `v0.1.0-preview.2` publishes
`0.1.0-preview.2`, while a `workflow_dispatch` can only move the pre-release suffix. Nothing is pushed
before it has been read back - `tools/release-packages.ps1` runs in CI on every push and again inside the
release workflow, checking the set of packages and what is inside each one. What a version contains, and
what a reader should not expect in it, is in [CHANGELOG.md](CHANGELOG.md); how a release is cut, and what
has to be true before the tag, is in [docs/release.md](docs/release.md).

## License

Apache-2.0, the same license as the original Munarium. See [LICENSE](LICENSE) and [NOTICE](NOTICE).