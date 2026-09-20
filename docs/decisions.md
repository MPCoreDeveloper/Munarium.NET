# Decisions

A decision is written down here with what was measured for it, so that the next person to ask does not have to
re-investigate, and so that a deferral reads as a decision rather than as an oversight.

## The deployment shape stays single-node, for now

**Decided:** 2026-09-20. **Status:** accepted for this version; the alternative below is deferred, not rejected.

### What was being decided

The original's artifact plane is not a local persistence mechanism. `datastore_builds.rs` binds artifacts into three
slots - `staged`, `shadow` and `serving` - with a generation per binding and compare-and-swap on every move, refuses the
serving slot to `bind` by contract, gates `promote` on a fleet of fresh ready nodes that already hold the staged
candidate, and mirrors serving-required versions under a pin-horizon policy. Delivering that needs four things a single
process cannot provide:

| What the plane needs | What the original uses |
|---|---|
| A shared catalogue with compare-and-swap | Postgres - `pg_pool()` is required for promotion |
| Node identity and readiness | `retrieval_node_snapshots` with `last_seen_at`, `admission_state`, plane and revision |
| Residency - which node has already opened a candidate | `retrieval_plane_expectations.minimum_open_nodes`, written by a readiness warmer |
| Shared artifact storage | `MUNARIUM_DATASTORE_ARTIFACT_STORE`: object store, `file`, `pg` or `mem` |

So the question was not how to implement routes but what shape this deployment has: one process, or a fleet with a
shared catalogue.

### What was measured first

SharpCoreDB already carries most of the substrate a fleet would need, which is what made this a real choice rather than
a choice between Postgres and nothing: `SharpCoreDB.Server.Core` has `NetworkServer`, `DatabaseService`,
`DatabaseRegistry`, `SessionManager` and `ClientSession`, `ConnectionPool`, `ServerHealthMetrics`, and tenants with
grants; `SharpCoreDB.Client` has connections, commands and transactions; and `SharpCoreDB.VectorSearch` carries an
`IArtifactStore` with a `LocalFileStore` implementation, plus `ArtifactManifest`, `ArtifactVerifier`,
`IVerifiableIndex` and `ArtifactCacheKey(IsolationDomain, LogicalVersionId, ...)`. That is the same stance the
original's verify takes - open the artifact and check every component, rather than reading the catalogue's own
projection - and transactions are what a compare-and-swap in the catalogue needs.

### The options

- **A - stay single-node, deliberately.** `index-artifacts`, `retrieval-rollout`, `index-build-jobs` and the mirror and
  shadow modes remain absent by design, and the contract measurement keeps them in the absent list.
- **B - build the mirror plane on SharpCoreDB server mode.** A shared catalogue served over `NetworkServer`, a node
  register with sessions as heartbeats and a readiness warmer, expectations, bind and promote with compare-and-swap,
  `IArtifactStore` for the artifact bytes and `ArtifactVerifier` for verification. This is the largest slice in the whole
  parity programme: the counterpart of `mirror.rs` (60 KB) plus `shadow.rs`, `shadow_exec.rs` and `shadow_candidate.rs`
  (48 KB) plus the datastore build, job and serving layers - and it changes the shape of the port, because a node becomes
  a first-class thing with an identity of its own.
- **C - adopt the original's topology.** Postgres plus an object store. The cheapest route to behavioural parity of this
  plane, and the one that contradicts this port's stack: SharpCoreDB is the data and graph engine here, and a second
  store would have to be carried alongside it.

### The decision

**A, for this version. B is worth doing in a future version and is planned on SharpCoreDB server mode rather than on
Postgres. C is rejected.**

The reasoning is about shape rather than effort. Everything this version is for - the ledger, the evidence plane, the
capability plane, retrieval and its persisted index, sessions and runbooks, authoring - is a *single deployment's*
capability, and it is complete or nearly so. The mirror plane exists to run several nodes of that deployment and to move
artifacts between them without a flag day, and nothing about the capabilities above depends on it: a deployment with one
node has nothing to mirror, and one that grows a second node will want a node register and a shared catalogue anyway.
Deferring it therefore loses no capability this version claims, and building it now would commit the port to a
deployment shape before there is a deployment that needs it.

What made B the right future route, rather than C, is that the substrate was measured to exist: a server with sessions,
tenants and health metrics, a client with transactions, an artifact store abstraction with a file implementation, and an
artifact manifest with a verifier. Choosing B later is a slice; choosing C later would be a second stack.

### What this means in practice

- Those routes are absent by design, not by oversight. `tools/spec-coverage.ps1` keeps counting them as absent, the
  coverage floor in `UpstreamContractTests` stays where it is, and `docs/sources-and-retrieval.md` records what the
  routes are so that the gap reads correctly.
- The local persisted index and its verification are unaffected: they are the local counterpart of the original's chunk
  table, and they stand on their own.
- Everything single-node stays in scope: authoring's drafts surface, the provider, report and run planes, the CLI and the
  matrix client - and `index-build-jobs`, which is single-node, carries the original's names, and is a prerequisite for
  any later fleet story because a fleet promotes what was first built as an artifact.
