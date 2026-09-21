# Providers and the wire

The detail behind the kernel table in the README. Each section is one row of that table, in the order it
appears there; the README keeps a one-line summary and the link.

[Back to the README](../README.md)

## Providers

The model-provider seam, with a deterministic in-process embedding provider for tests and smoke runs; the provider
plane, which keeps declarations and probes them; and the
evidence hierarchy's two real planes. `FactProvider` reads a pinned memory version's current facts —
optionally narrowed to a scope prefix — and refuses when a layer names no version. `MatrixProvider` speaks
REST to a semantic plane: the view-to-contract mapping stays in the profile, the request carries a contract
name with typed parameters or names drawn from lists the view declares and never the turn's question, the
authorization is the intersection of the session's and the view's, a 4xx is the plane answering correctly
rather than an outage, an outage trips a per-instance circuit breaker, and the parser is held against the
examples that ship with the contract.

## The provider plane

The registry is a place declarations are kept, and a declaration is a dialect, an endpoint and the model names that
dialect serves - which is all the original's registry row carries, measured rather than assumed. Where the credential
lives is named as a location and read by an adapter at the moment of a call: a `credentialRef` is an environment
variable's name or a file's path, never material, and `credential_ok` answers whether it resolves right now. That is the
whole of this port's answer to a plane that needs keys - a registry that held a credential would make the ledger the
place secrets are kept, which is the one place this port has said they do not go.

Three things about it are worth knowing. A name is reserved: `default` is the default-provider rule rather than a
configuration, so applying it is refused by name. Three declarations are synthesized rather than stored -
`default-anthropic`, `default-openai` and `default-openrouter`, each backed by the family's conventional environment
variable - and they are listed and probed like any other, which is what makes the plane legible before a deployment has
applied anything. And a tier resolves through the configuration first and the family's built-in table second, so a
one-line declaration answers a three-tier runbook while a configuration that pins `models.fast` still wins.

A probe is what a deployment can actually reach, observed rather than assumed. It goes to the adapter this deployment
holds for that family - an `IModelProvider` a deployment composes, since the kernel never touches a network - so a key
that does not resolve is reported by the location it should be in, and a family with no adapter is reported by family.
Both are answers rather than errors: an operator asking about a configuration wants to be told what is missing, by name.
`/healthai` probes the built-in tier models instead - three reachable families, three tiers each - where a family whose
credential is unset is *skipped* rather than failed, and the plane counts as healthy only when at least one check ran and
every check that ran passed. A deployment with nothing configured therefore reads as not healthy rather than as
vacuously healthy, which is the original's own rule.

The document is read by `ProviderConfigReader`, which sits beside the runbook reader because that is where this port's
YAML reader is - the one that maps nodes by hand - and two of its choices are this port's rather than the original's. A
field the reader does not know is refused rather than ignored, because a misspelled `endpont` would otherwise silently
use the dialect's own endpoint; and a budget that is not a positive number is refused by name, because a ceiling nobody
could enforce is worse than a refusal.

### The relay, and the ceiling it is held to

`POST /v1/providers/{name}/complete` makes a deployment spend its own credential on a caller's behalf, so the access
capability is resolved **in front of** the route - on both transports, through the same gate the ingestion plane uses -
before a configuration is looked up, let alone called. What follows is an order rather than a sequence of statements:
the configuration resolves (the reserved `default` name engages the default-provider rule, which takes the first family
whose credential resolves *and* which this deployment holds an adapter for), the model resolves as an explicit model,
the tier the caller named, the configuration's own first model, then the family's capable built-in, the ceiling is read
from `complete_default`, the configuration's declared budget is checked, and only then is the adapter called. What comes
back is not turn evidence and is recorded nowhere.

Three things are refused or reported by name rather than approximated. `version_id` names an invocation to record, and
this port has no invocation-provenance plane to record one in, so a call that asks for it is refused instead of being
made unrecorded. `embed` is JSON-only: its vectors are an array of numbers inside an array of vectors, which has no
faithful protobuf form, so the generated method answers `Unimplemented` with that reason - the same answer the evidence
row read gives. And `cache_hit` is always false, because this port keeps no embedding cache: the field is reported
rather than invented.

The ceiling is `GET`/`POST /v1/max-tokens`: one object of the original's eight per-call output-token ceilings, with the
process's `MUNARIUM_MAX_TOKENS_*` variables over the built-ins and a tenant's replacement in front of both. It is read
where the calls are made rather than reported from somewhere else: a session turn's answer (`turn_completion`), its query
expansion (`query_expansion`), its intent classifier (`hierarchy_classifier`), a runbook validation's advisory pass
(`runbook_advisory`), the guided-authoring assist (`authoring_assist`), each provider probe (`healthai_probe`) and a
relayed completion (`complete_default`). A process variable that is not a usable ceiling stops the deployment at
composition rather than being quietly ignored, and a tenant's set is replaced whole, so no caller can change one ceiling
by accident. `hierarchy_intent` is carried and replaceable and read by nothing yet: this port's hierarchy resolves intent
in one classification call where the original makes two.

A configuration may also declare `budgets` - requests and tokens per minute, and a daily token ceiling per tier - and the
relay enforces them: a rolling minute for the rates and a UTC day per tier, with an estimate checked before the call and
what the call actually cost recorded after it. The original divides a configured ceiling across replicas and draws its
daily ledger from a shared store; this deployment is one node by decision, so the window lives in the process that
enforces it and a restart resets it. That is written here because it is the one behaviour a deployment could otherwise
only discover by moving a workload onto it.

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
