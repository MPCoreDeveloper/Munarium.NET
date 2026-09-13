# Credits and attribution

## Munarium — the original work

**Tyler Jensen** — [@tylerje](https://github.com/tylerje) · tyler@tsjensen.com
**Original repository:** [github.com/iokaio/munarium](https://github.com/iokaio/munarium)
**Published by:** Ioka LLC, under the Apache License, Version 2.0.

Tyler Jensen designed and built Munarium: the governed-memory service and the Munarium Memory
Protocol (MMP) it speaks over REST and gRPC; the append-only fact ledger with governance in the
write path and supersession chains; the `as_of_seq` pin; the provenance envelope carried on every
answer; the declarative shape and runbook systems; the Matrix structured-evidence plane; the
conformance suites that act as the executable specification; and the client libraries in Rust,
Python, .NET and Java.

Munarium.NET exists only because that design was published openly under a permissive license.
The architecture, the protocol, the invariants and the vocabulary in this repository are his.

> **If you want the original and canonical implementation, use
> [github.com/iokaio/munarium](https://github.com/iokaio/munarium).** It is the source of truth
> for the design, and it is actively maintained in Rust.

## Munarium.NET — the C# port

Maintained by **MPCoreDeveloper**. An independent re-implementation of the Munarium design on
.NET 11 / C# 15. It is not affiliated with, endorsed by, or maintained by Ioka LLC, and it does
not track the upstream repository.

Built on the MPCoreDeveloper .NET 11 stack:

| Component | Used for |
|---|---|
| SharpCoreDB | embedded storage, vector search, FTS and GraphRAG |
| SharpDispatch | the zero-allocation CQRS command path |
| SharpPortico | OpenAPI → gRPC code generation and the gRPC↔REST proxy |
| Posseth.UlidFactory | spec-compliant ULID identifiers |

## Trademarks

"Munarium™" and "Ioka™" are trademarks of Ioka LLC, claimed through use. They appear here only to
describe the origin of the work, which the original
[TRADEMARK.md](https://github.com/iokaio/munarium/blob/main/TRADEMARK.md) permits.

## License

Apache-2.0, the same license as the original Munarium. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
