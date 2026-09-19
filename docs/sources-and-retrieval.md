# Sources, retrieval and index versions

The detail behind the kernel table in the README. Each section is one row of that table, in the order it
appears there; the README keeps a one-line summary and the link.

[Back to the README](../README.md)

## Sources

A document's identity is its logical path and not its content hash, so the same bytes at two paths are two
sources and a path prefix can bind a collection; the hash stays as integrity, recorded and surfaced in
provenance. Constructing a key validates the path (byte-bounded, no traversal, no absoluteness, no Windows
drive, no empty or dotted segment) and document ingress refuses the reserved `evidence/` keyspace outright -
as a separate predicate, so the evidence writer that legitimately builds keys there has no check to bypass.

## Retrieval

A retrieval seam that returns a `ProvenanceEnvelope` rather than bare similarity. The adapter over SharpCoreDB
runs two independent legs — Okapi BM25 through the engine's own analyzing tokenizer, stemmer and stop words,
and a vector leg that is either an exact scan or a DiskANN Vamana graph — and fuses them **by rank**, because
a BM25 score and a cosine distance do not share a scale. Fusion is the port's own so the answer and its
envelope are produced together: the ranking that decided the answer is the ranking the envelope records, and
opaque chunk ids stay opaque where the engine's fusion parses them back to numbers.

## How a document becomes text

The wire carries a document's **text** or its **bytes** (base64, exactly one of the two), and the seam that turns either
into text is `TextExtractor`. A DOCX is read out
of its own XML — a `.docx` is a zip the base class library opens, so no model, no native library and no rasterizer are
involved — and a PDF's text layer is read through PdfPig, which is pure managed and therefore survives a NativeAOT
publish. That last part is not taken on trust: the AOT smoke tool extracts text from a hand-written PDF inside the
native binary CI publishes for every platform, so a codegen hazard in the parser fails the build rather than production.

What the seam will not do is invent text. A media type no extractor reads is refused and the document is not stored,
because a source that can never be retrieved is worse than a refusal. A PDF with no text layer is a scan: it reads as
**empty**, which is the honest answer and the signal the OCR path keys on — and OCR is the one capability this port has
not ported, since upstream runs it on a local inference runtime whose model files this port cannot load. A page whose
embedded font carries no Unicode mapping yields that font's own codes instead of words, which no text-layer reader can
fix; that is a limit this port shares with the original. The extractor set is versioned — `extract@1[docx@1,pdf-text@1]`
— and joins an index version's identity, so improving how a document becomes text produces a new version rather than
silently different chunks under the same name.

## Index versions

An index version is an immutable snapshot of one collection's corpus as one shape sees it, and its identity is
a hash of everything that determines what a query would match — collection, shape, engine, chunker,
extractors, embedder and the source/hash bindings. So a rebuild of one corpus is one version and any change
that alters the text or the vectors is another, which makes a build idempotent and a cutover meaningful.
Segments are joined with a unit separator and the sources are sorted and deduped before hashing: the same bug
the evidence plane's domain key fixed, where material joined with a printable character can be reached two
ways. Building records a version **without** making it live, a cutover is per collection and atomic, a
superseded version stays resolvable, and `ResolveAsync` takes an answer's envelope and checks that the version
it names exists, that the bytes it cites are ones that version indexed, and that it does not claim more of the
ledger than the index ever reflected.
