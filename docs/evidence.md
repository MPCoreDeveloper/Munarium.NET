# The evidence plane and its hierarchy

The detail behind the kernel table in the README. Each section is one row of that table, in the order it
appears there; the README keeps a one-line summary and the link.

[Back to the README](../README.md)

## Evidence

A manifest is a promise about bytes: the contract version and canonicalization, the artifact and
logical-result hashes, the schema, the row identity rule, the snapshot vector, and the authorization class.
The domain key excludes the artifact hash — re-serializing one logical result must not mint a second artifact
— and includes the authorization class, joined with a unit separator, so one compartment holding a comma
cannot be two compartments. Content is verified canonically (`sha256:` plus lowercase hex), and a check
reports the two lengths and the two hashes rather than a bare yes: an operator chasing a mismatch needs to
know what was expected and what arrived.

## The evidence hierarchy

A research profile resolves into a plan of layers, each with pinned sources, a requirement (`required`,
`optional`, `fallback`), a role and its own context budget. `HierarchyRunner` runs them in trust order:
providers are tried in order and the first that claims a source wins, a fallback layer runs only when nothing
before it produced evidence, and a plane-qualified source (`matrix:`, `facts:`) that no provider claims
**refuses** rather than quietly becoming a document search that reports a required layer satisfied. A required
layer that refused stops the turn — as a result rather than an exception, because a refusal is something the
answer has to disclose. `HierarchyComposer` then composes the blocks: highest trust occupies the budget first,
a `preserve_complete_result` layer is taken whole or dropped, every block is labelled `COMPLETE` or
`TRUNCATED`, and rows are numbered by one function the served-evidence list shares, because a checker that
numbered rows differently from the text the model read would reject correct citations. A profile is
declarative and is checked **before** it is applied: every source has to be a declared collection, data view
or fact scope; a name may not contain `:` because that is what a source prefix is spelled with; a `facts:`
layer has to name its version, since a bare one could only ever refuse; and a required whole-or-nothing layer
whose declared size cannot fit the budget it will be composed under is a contradiction in the document, caught
at apply time rather than discovered one turn at a time. A turn naming a profile nobody declared fails closed
rather than falling back to the document path.
