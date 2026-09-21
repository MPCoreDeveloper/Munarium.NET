# How this port is checked

The original is the specification, and the way to know what it does is to read it. That sounds obvious, and it is not:
each of the four corrections below was written down as a **capability this port lacks**, and each turned out to be a
claim nobody had opened the source to check.

## The rule

When something looks impossible to port, or when a gap is explained by "upstream has a capability we do not", the next
step is to read the original and measure - not to restate the explanation. A gap is a gap only once the original has
been read and this port has been asked the same question.

## What that found

| Claim that had been written down | What the original actually does | What changed |
|---|---|---|
| "A PDF or a DOCX is refused by name, since the upstream extractors depend on a model capability this port has not ported." | A `.docx` is a zip holding `word/document.xml`. No model and no native library: the base class library opens it. | DOCX extraction ported, and the seam's own documentation corrected. |
| "A PDF's text layer needs a PDF parser this port does not have." | It needs *a parser*, not a model. PdfPig is pure managed, and a NativeAOT binary with it in the graph extracts - measured, on fixtures and on a real 15-page paper, against pypdf. | The text layer ported, and the AOT smoke tool now extracts from a PDF on every platform CI publishes. |
| "Upstream resolves entities through a model capability this port has not ported." | Across its whole tree `Entity` appears twice - the struct and the field on the snapshot - and the snapshot builder hard-codes `entities: Vec::new()`. Nothing constructs one, and no model is involved anywhere. | The claim replaced by the measured one: parity with an empty plane. An `entity.resolved` event reaches the plane here, which is tested. |
| "OCR is the one capability this port cannot have." | Upstream has two opt-in routes: local `ocrs` + `rten` behind an off-by-default feature, with `.rten` models fetched at startup; and a `DocumentIntelligence` trait whose provider lives in an adapter crate - escalation-only, billed per page, optional. | The local engine stays out, because no pure-managed .NET OCR exists and a native one contradicts the AOT job. The shape is portable: a trait in core, the dependency in an adapter. The scan half already works - PdfPig hands over a scan page's image byte-identical and still encoded. |
| "Pagination is missing: the contract needs `PageRequest` and `PageResponse`." | The original has no pagination at all. Three list routes carry a fixed `LIMIT 50` or `LIMIT 500`, and there is no page token, cursor or page size anywhere in its tree. | Nothing to port - and this one was the port's own plan rather than its documentation, which is the same lesson one layer up: a plan is a claim too. Pagination here would be an extension, not parity, and that is a decision nobody has made. |
| "What is not there is a persisted index: the chunks live in the process that built them ... a very large corpus pays for that rebuild at every start", and "the one thing in flight is the per-collection probe". | The chunks of a version had been persisted - one table per version, loaded by `IndexBuilder.LoadAsync` - and the per-collection probe was in the turn runner, both several commits before the paragraph that called them missing was written. The test count in the same paragraph was recalled (760) rather than measured (921), and the coverage number was absent altogether. | The paragraphs replaced by what the code does, the in-flight note removed, and the status now carries two numbers that were *run* rather than remembered: the suite's own total, and the covered-operation count from `tools/spec-coverage.ps1`. |

Two of those also broke the *documented* contract, not just the code: `PUT /v1/sources` promised in its own description
that a PDF would be refused by name. A claim that reaches the OpenAPI description reaches every generated client, so it
is worth reading the description as carefully as the code.

## The other half of the rule

Reading the original is not only for refuting claims. It is also where the shapes come from, and they are ported rather
than invented: the two-forms-one-route idiom (a route told apart by which form is present), the escalation-only
document-intelligence contract, a source row that records how extraction went, and an extractor set that joins an index
version's identity. When a port-side shape is still uncertain, the original is where it gets settled.

## What the gate actually checks

This port's own checking had a hole in it, and it was found the way the table above describes: by measuring instead of
believing.

A commit went out whose `Munarium.Server` build failed locally with four `CA2016` errors - a cancellation token a plane
never forwarded - and CI reported success on that exact commit. Explaining it took three measurements: the CI log for
that job carries the same diagnostic **sixteen times, as a warning**; the job's log never mentions the property that
turns warnings into errors; and the local run on the same tree, the same pinned SDK and the same solution fails. The
conclusion is that the Sonar-wrapped build step reports analyzer findings without failing on them, while
`Directory.Build.props` sets `TreatWarningsAsErrors` only when nothing has set it already.

What that changes here: **a green CI is evidence that the solution compiles and that the tests pass - it is not evidence
of a warning-free build.** `dotnet test -c Release` on a developer's machine is the stricter gate, and it is the result
to quote. The lesson is the one the five corrections above taught, one layer up: the pipeline had been treated as the
measurement, and what it measures had not been read.

## A test that asserts state is asserting an order

The first build of this version failed on the runner and nowhere else: `Failed: 1, Passed: 158`, with the reason in a
platform log the runner took with it. Every run of that code failed the same way - the build, its re-run, and a run of the
same tests from a branch - which ruled out a flake, and the local runs ruled out the change being wrong: the same commit
on Windows, and on Linux in WSL, the suite and the whole solution, repeatedly, pinned to one core and to two. The log was
dragged out of the test results only after the pipeline was taught to upload them, and it said:

```
failed ProviderRelayApiTests.TheCeilingsAreReadAndReplacedAsOneSet (19ms)
  Assert.Equal() Failure: Strings differ
  Expected: "environment"
  Actual:   "tenant"
```

The claim was that a deployment which has replaced nothing reports the ceilings of the process it was composed in. It
was true, and it was made in the class that also replaces those ceilings forty lines below it - so what the test
actually asserted was the order the tests happened to run in, which is not part of any contract. Two machines, one
commit, two orders, and a build that was green on one of them: that is what an ordering assumption looks like from the
outside.

What changed:

- The claim moved to a class with a database of its own, which never replaces anything, so "nothing has been replaced"
  is true by construction rather than by luck.
- It stopped comparing against the built-ins and computes the process composition instead, so it holds the endpoint to
  what it promised rather than to what the machine happens to set in `MUNARIUM_MAX_TOKENS_*`.
- A failing suite now prints the platform log and uploads it. That is the part that generalizes: the missing evidence
  cost more than the bug, because a red build reporting "1 of 160 failed" cannot be acted on by anyone who did not
  write it.

The lesson is the one this document keeps arriving at, one layer further in: state and order are both claims, and a test
that reads a pristine value out of a shared deployment is testing neither.

## Never anchor an insert on a doc comment

A text anchor that sits on, or immediately after, a line of documentation splits the block it belongs to. The symptom is
not a missing type: it is a type whose documentation describes a different type, which the compiler reports as a param tag
for a parameter that does not exist. It has cost three separate incidents - the export doc comment, and the wire models
twice - and it fails in the direction that wastes the most work, because the build output points at the documentation
rather than at the insert.

Two rules that remove it entirely:

- Append at the end of the file when the language allows it, which for a file-scoped namespace it does.
- Otherwise anchor on an attribute or a code line, never on a documentation line, and re-emit the anchor in the
  replacement so it cannot be consumed. An editor replacement consumes its anchor; forgetting that is how the export doc
  comment was lost.

The same class of mistake as reusing a count from memory: an anchor or a count recalled rather than read is a claim, not a
measurement.
