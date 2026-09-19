# Ledger, governance and the shapes a claim has

The detail behind the kernel table in the README. Each section is one row of that table, in the order it
appears there; the README keeps a one-line summary and the link.

[Back to the README](../README.md)

## Ledger

`IStorageBackend`: a head read, an optimistic-concurrency append, a stream read and a global read.

## Governance

The write path judges before it writes. A gate returns `Permitted` or `Blocked`, and a blocked claim is
recorded as *disputed* rather than dropped — a refusal that cannot be recorded is not governance. The command
path ships two gates: `shape` (a body that does not satisfy its shape) and `ledger-conflict` (a claim that
would overwrite what its lineage already holds without saying so); a claim says what it is doing through
`claim_type`, so an *update* or a *correction* supersedes on purpose and an unnamed claim does not.

## The gate vocabulary

All six rule families, as pure functions of a pinned snapshot and a candidate unit: `anchor-consistency`,
`ledger-conflict`, `orphaned-reference`, `meta-leakage` and `lexical-similarity` always run together (with the
anchor finding subsuming the conflict for the same detail), and the `chronology-*` family — order,
containment, overlap, deadlines, durations — runs when a deployment declares rules. A withheld claim is
reported as a finding carrying its rule id, severity and the claim key the accept path disputes, and a write's
findings are recorded in the same append as the claims it judged, so a verdict and the write it belongs to
cannot drift apart. Only the newest candidate-side contributor is named, so a verdict never blames the
ledger's own history.

## Identities

The ledger's identities are ULIDs, so one value is three things: what a claim *is*, *when* it happened (a
48-bit millisecond count inside the first ten characters), and *how it sorts* against its neighbours
(Crockford Base32 preserves order, so ordinal comparison is time comparison). The kernel therefore never reads
a clock — `MeshSnapshot.WrittenAt` is derived from the identities the snapshot holds — which is what makes a
chronology finding reproducible by anyone who holds the snapshot. The order an identity carries is a *time*
order and deliberately not the ledger's: the pin is a position the store assigned, because two writers whose
clocks disagree would otherwise resolve the same fact differently. So `ClaimResolution` keeps reading
sequences, and the identity order is what a caller uses when it has identities but no positions.

## Claims and resolution

The semantic model the gates reason over: `subject.key=value` with the scope it was written in, its
provenance, and the claim it supersedes. `ClaimResolution` is the reference implementation of the ledger's
read semantics — the superseded set is itself filtered by the pin, so a claim superseded only *after* the pin
still reads as current at the pin. Every storage backend's query has to agree with it.

## Anchors, promises and counters

A locked detail may not drift (`anchor-consistency`); a promise made in one scope is owed to a later one, and
one fulfilled after the pin reads back *open*; a counter is a whole-document frequency with an optional
ceiling, and the writer is told what is left rather than only what it overspent.

## Digest ladder

Deterministic, model-free compression rungs: tier 0 per scope, tier 1 per scope-prefix group with the values
elided, tier 2 the whole-lineage rollup. Rungs are *rebuilt* from the pinned facts rather than served, because
stored digest text has no history to read at a pin.

## Chronology

A closed calendar grammar (ISO dates, `YYYY-MM`, `YYYY`, month names, ranges, seasons, and
`circa`/`approx`/`~` hedges) and the certainty algebra on top of it: `DefinitelyBefore` is true only when the
comparison is certain given both precisions and both hedges, so an intentionally approximate date is never a
violation by itself.

## Shapes

Versioned, declarative shapes: a JSON Schema for the fact body, and the identity fields that decide
supersession. A schema violation is a verdict, so the claim lands in the ledger with its reason attached.
Validation is a documented, deterministic subset of JSON Schema implemented in the kernel — no parser library
and no reflection, so the errors are stable enough to hash.

## Facts and pins

Canonical fact encoding, supersession along a lineage, and an `as_of` pin that rebuilds the same slice — and
the same SHA-256 digest — every time. A fact carries the version it was written to and the body it was claimed
with, so a slice can be read back into the claims it came from and one version can be read out of the whole.

## Versions and lineage

A version is an ordinary claim under the `version` shape, so it is judged by the same gates and rebuilt from
the same slice as everything else — and its identity is immutable by construction, because claiming it twice
is a ledger conflict. `GetLineage` walks parent links root-first, and `as_of_date` resolves to a pin through
the version's own metadata.

## Context

`ComposeContext` composes what a model would be given: the accepted facts of one version or shape, within a
token budget, with refused claims listed separately under *Disputed*. It is a pure function of the pin, so the
same pin composes the same text and the same `content_hash` — which is what makes the hash a cache key rather
than a guess.
