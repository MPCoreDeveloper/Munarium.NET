# Providers and the wire

The detail behind the kernel table in the README. Each section is one row of that table, in the order it
appears there; the README keeps a one-line summary and the link.

[Back to the README](../README.md)

## Providers

The model-provider seam, with a deterministic in-process embedding provider for tests and smoke runs; and the
evidence hierarchy's two real planes. `FactProvider` reads a pinned memory version's current facts —
optionally narrowed to a scope prefix — and refuses when a layer names no version. `MatrixProvider` speaks
REST to a semantic plane: the view-to-contract mapping stays in the profile, the request carries a contract
name with typed parameters or names drawn from lists the view declares and never the turn's question, the
authorization is the intersection of the session's and the view's, a 4xx is the plane answering correctly
rather than an outage, an outage trips a per-instance circuit breaker, and the parser is held against the
examples that ship with the contract.

## Wire

One OpenAPI specification as the contract, one transport-agnostic operation surface behind it, and both
surfaces served from it: JSON/HTTP by `Munarium.Server`, and gRPC/protobuf by the service base SharpPortico
generates from that same specification - so the two cannot drift on names, shapes or enum values. A new
operation is one specification entry plus one adapter per surface; the generated client is what the gRPC tests
drive, which is why the contract file is the only place an operation is declared. The runbook operations are
the first ones to cross the whole wire — applying what an operator wrote, and listing what the deployment
holds — and they are deliberately the smallest pair that does: the apply is the catalog and adds no rule of
its own, the refusal's status comes from the problem so both transports answer the same failure the same way,
and the status enumeration crosses member by member rather than by a cast, because a cast would follow
whichever numbering each side happens to use.
