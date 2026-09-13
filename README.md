
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
  version conflict does not compile.
- **NativeAOT-compatible and platform-independent** — the libraries declare AOT/trim compatibility,
  so reflection and codegen hazards fail the build, and CI publishes and runs a fully AOT-compiled
  binary on `linux-x64`, `win-x64` and `osx-arm64`.
- The .NET analyzers and `SonarAnalyzer.CSharp` run on every build; **warnings are errors** in
  shipping code, and code style is enforced during the build.
- Package versions live in one place (Central Package Management), and the whole stack is dogfooded
  rather than merely described.

## What is in the kernel so far

`src/Munarium.Core` holds the seams and the kernel types; the adapters live beside it.

| Piece | What it does |
| --- | --- |
| **Ledger** | `IStorageBackend`: a head read, an optimistic-concurrency append, a stream read and a global read. |
| **Governance** | The write path judges before it writes. A gate returns `Permitted` or `Blocked`, and a blocked claim is recorded as *disputed* rather than dropped — a refusal that cannot be recorded is not governance. |
| **Shapes** | Versioned, declarative shapes: a JSON Schema for the fact body, and the identity fields that decide supersession. A schema violation is a verdict, so the claim lands in the ledger with its reason attached. Validation is a documented, deterministic subset of JSON Schema implemented in the kernel — no parser library and no reflection, so the errors are stable enough to hash. |
| **Facts and pins** | Canonical fact encoding, supersession along a lineage, and an `as_of` pin that rebuilds the same slice — and the same SHA-256 digest — every time. |
| **Retrieval** | A retrieval seam that returns a `ProvenanceEnvelope` rather than bare similarity, and reciprocal rank fusion for combining a vector leg with a lexical one. |
| **Providers** | The model-provider seam, and a deterministic in-process embedding provider for tests and smoke runs. |

`src/Munarium.Store.SharpCoreDb` is the storage and retrieval adapter over SharpCoreDB, and
`src/Munarium.Providers` holds the providers.

## Status

Early days: this repository is the C# port in progress, and the kernel (`src/Munarium.Core`) is the
first piece of it. The design it follows — and the executable specification it will be held to —
is described and proven in the [original project](https://github.com/iokaio/munarium).

## License

Apache-2.0, the same license as the original Munarium. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
