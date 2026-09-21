# Turns, sessions and applied runbooks

The detail behind the kernel table in the README. Each section is one row of that table, in the order it
appears there; the README keeps a one-line summary and the link.

[Back to the README](../README.md)

## Turns

`TurnPipeline` is the kernel's half of a turn, and it resolves nothing: the plan, the template, the model and
the budget all arrive already chosen, because which model, which keys and which tenant are a deployment's
business. Where the budget comes from is the deployment's ceiling unless the runbook names its own: a turn's answer, its query expansion and its intent classification each read their ceiling from GET /v1/max-tokens, so what an operator sees there is what a turn is actually held to. What it owns is the order, and the order is the design. Evidence first — a required layer that
cannot answer stops the turn **before** a model is paid for, which is the whole point of `required`. Then one
completion, then the checks over the answer, and the checks are pure string work over data the turn already
holds, so a violation is a fact rather than an opinion. A quoted span shorter than fifteen characters is not a
grounding claim and passes; a substantial one has to resolve inside the served text once whitespace is
normalized; and a bracketed label has to contain a slash — so `[sic]` and `[1]` are nobody's business — and
name something the turn served, either a chunk label the context printed or a path a hit came from. A
violation buys a corrective completion carrying the violations, the answer they were found in and the original
task re-attached, because the retry is a re-ask rather than a targeted fetch: the second attempt has
everything the first had, which is what makes "quote only what is here" an instruction a model can actually
follow. Retries are clamped at two however a runbook phrases it, and an answer that still violates stands with
the violation recorded rather than hidden. A model that stops early — a reasoning model spends hidden tokens
from the same ceiling and can exhaust it before any visible text, which is why an empty answer counts as
truncated — buys exactly one re-ask at four times the ceiling on the identical prompt, and a corrective retry
then rides that raised ceiling. Tokens are counted across **every** call, not just the one that produced the
answer that stands.

## Sessions

A session is a conversation with a memory of what it may see. It pins the runbook it was opened over as
`name@version`, so the next version of a document cannot silently change what an open conversation searches,
and it snapshots the clearance it was opened with, so a token that changes or a runbook that is upgraded mid
conversation never alters what the turns in flight can read — and the clearance is a value rather than a
special case, with the compartment rule being **all**: every tag a collection requires has to be carried, and
a context that clears every compartment clears the gate outright instead of being special-cased at each gate.
Opening refuses twice, and the second refusal is the interesting one: a clearance that does not name the
runbook is refused by name, and a clearance that covers the runbook but none of its collections is refused
rather than opened empty, because an empty session would answer every question with a confident nothing and
the caller could not tell that apart from an empty corpus. A turn is a row: the ordinal, who asked, what was
asked, which collections were searched after access filtering, the merged hits, the per-collection envelopes,
the completion audit and the hierarchy decision — on the turn rather than inside the audit because the audit
capture is capped, so exactly the large layered turns whose bodies get summarized away are the ones whose
decision has to survive somewhere else, while a turn that ran no profile records absence rather than an empty
decision that would claim a hierarchy ran and decided nothing. The ordinal is the store's answer and not the
caller's request: the original allocates it inside the insert and retries a duplicate-key violation, while
`SharpCoreDbSessionStore` allocates under the adapter's lock, which is the same guarantee inside one process
and the only one this engine allows — a declared primary key is a way to lose rows here (measured) — and that
difference is written down where it lives rather than left to be rediscovered. Sessions and turns sit in
tables of their own, because a conversation is not a claim about the world and asking a question is not a
verdict: keeping a transcript out of the ledger keeps it out of the record governance reasons over.
`SessionTurnRunner` is the order a turn runs in, and it is the order rather than the code: a closed session is
refused before anything is read, then the intent (the plan is a function of the question, and a runbook that
pins no `intent` task costs nothing there), then the evidence — where a required layer that cannot answer
stops the turn before a completion is paid for — then one answer with its checks, then the record. One search
per turn however many layers ask for it, and the hits recorded are the hits the model read. The document path
has its own half of the pipeline rather than a weaker copy of it: `TurnPipeline.AnswerOverTextAsync` gives a
turn that ran outside any profile the same quote and citation checks, the same truncation re-ask and the same
bounded repair, while its outcome carries **no** decision rather than a synthetic one — an audit that cannot
tell "no profile ran" apart from "a hierarchy ran and decided nothing" is not an audit. The progress stream
makes that order visible rather than merely asserted: `POST /v1/sessions/{session_id}/turns/stream` reports a
`progress` event at each stage boundary the turn actually crosses - the model it resolved, the one retrieval,
a profile's layers and their coverage, the composed context, each paid completion and the check that read it
back - and ends with exactly one `done` carrying what the unary route would have answered, or an `error`
carrying the problem, because a stream that has already sent its status line cannot be told anything else. It
reports the original's vocabulary in full: `expansion` is a paid call a runbook declares, and `probe`, `selection` and
`retrieval` come from a retrieval path that probes collection by collection and searches each one in turn - which is
what a turn over a deployment with an index per collection does, and a deployment that built one corpus reports the
retrieval alone rather than a stage it never crossed. The stream is JSON-only, and by construction rather than by convenience: the
original's own protobuf carries no streaming
method for sessions, so the generated gRPC method refuses by name rather than answering with one event that
would look like a turn that reported a single stage.

## Applied runbooks

A runbook is applied as YAML and kept as one row per version, because the reference is `name@version`: a
session pins one, and a pin whose own document could be outlived by a newer one would not be a pin. Resolution
is by reference or by name, and the two mean different things — a caller that named a version has already
decided, and a caller that named a bare name wants the newest usable one compared as a **number**, since with
versions in the double digits a string comparison answers 9 for 10. Applying is a gate for shape and not for
wisdom: a document that cannot be read is never stored (the refusal carries the reader's own complaint,
because the operator editing YAML wants the line), while the findings are what an operator reads before
applying — refusing on a warning about a cutover that will publish without a human would be refusing a runbook
that runs. Removal takes two passes and deletes nothing: the first pass arms the row with an identity and
leaves the version usable, only the identity that armed it may confirm, and re-applying resets an armed
removal because the bytes changed. A removed version is refused rather than resurrected — the message says
publish a new version instead — so the turns that ran under it keep naming the document they actually ran on.
Resolving a name re-reads the stored YAML rather than a mapping cached beside it, and a document that no
longer maps is reported as unresolvable instead of being served from a mapping a reader has since learned to
refuse.
