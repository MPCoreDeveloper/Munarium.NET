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

Two of those also broke the *documented* contract, not just the code: `PUT /v1/sources` promised in its own description
that a PDF would be refused by name. A claim that reaches the OpenAPI description reaches every generated client, so it
is worth reading the description as carefully as the code.

## The other half of the rule

Reading the original is not only for refuting claims. It is also where the shapes come from, and they are ported rather
than invented: the two-forms-one-route idiom (a route told apart by which form is present), the escalation-only
document-intelligence contract, a source row that records how extraction went, and an extractor set that joins an index
version's identity. When a port-side shape is still uncertain, the original is where it gets settled.
