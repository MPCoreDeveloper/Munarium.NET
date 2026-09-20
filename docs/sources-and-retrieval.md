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

The row records how the extraction went, which is the original's answer to a document that contributes nothing:
`extraction_status` is `ok`, `empty` or `failed` and `extraction_method` is `text`, `docx`, `pdf-text` or `ocr`, both
served on the source info and both written where the document is extracted — at ingest here, at index time there, which
is the same moment in this port because its ingest indexes. A re-upload clears them, because the new bytes have not been
read yet. A build whose extraction failed records that and indexes nothing for that source rather than stopping, which is
what the original does with an extraction error: the failure is data, and a corpus quietly missing one document is visible
in the rows instead of only in a log.

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

## The persisted index, and what the engine already does

This port rebuilds every live index version at startup, and that is a cost rather than a design - the README says so,
and the reason is measurable. `SharpCoreDbRetriever` holds a `List<IndexedChunk>`, a `FullTextIndex` and an `IVectorIndex`
(FlatIndex or DiskAnnIndex) in the process, and the host opens every version empty, so the chunks, their text and their
vectors all have to come back from the source rows.

The original does not work that way. Its `munarium-retrieval-pg` writes one row per chunk - `(chunk_id, source_id,
source_hash, ordinal, text, embedding)` - into Postgres, with pgvector columns and HNSW indexes (GIN for the lexical side),
cascading per partition. Its index *is* its database, so a restart is a reconnect.

The engine underneath this port can hold that too, and it does not have to be changed for it:

| What the gap needs | What SharpCoreDB already has |
|---|---|
| A column that holds an embedding | `ColumnType.Vector`, documented as a fixed-dimension float32 array for similarity search |
| An index over it | `CREATE VECTOR INDEX name ON table(column) USING FLAT\|HNSW\|DISKANN[(params)]` |
| The index definition to survive | That statement stores it in the table own metadata, for the VectorSearch module to consume |
| The index itself to survive | `VectorStorageFormat.VectorIndexPrefix` - a persisted vector index under the engine storage |
| A query that uses it | An `IVectorQueryOptimizer`, invoked when a query can be answered by a vector index |

So the slice is: a chunk table per index version, a `Vector` column holding the embedding, `CREATE VECTOR INDEX` over it,
the chunk rows written as a build indexes them, and recovery that loads what is there and rebuilds only what is missing.
The table name carries the version because a predicate can compare an identity and nothing else (measured: caller-supplied
text does not compare), so the version is keyed by a derived value rather than by its own text.

One thing stays in the process: the lexical leg. `FullTextIndex` is an in-process class and no persisted full-text index
was found anywhere in the engine (measured, by searching the checkout). The chunk text is in the table, so the lexical
index is rebuilt from it at startup - no extraction and no embedding, which is where the cost was. If the engine ever grows
a persisted full-text index, this is the one place that would use it.
### What the engine split means for this port

Reading the checkout rather than the package documentation sharpened three things.

A vector column is real and a table accepts it: `Table.cs` resolves a type whose name starts with `VECTOR`, the DDL parser
does the same, and a value parses through `ParseVectorValue`, so chunk text and its embedding can sit in one row.

The query path is split on purpose. The core DML leaves vector optimization to the extension module - its own
`TryExecuteVectorOptimized` returns null - and `SharpCoreDB.VectorSearch` is where the work happens: `VectorSearchExtensions`
registers it, `VectorIndexManager` owns the indexes, `VectorQueryOptimizer` is the hook the core calls, and
`VectorTypeProvider`, `VectorFunctionProvider` and `VectorSerializer` carry the type, the functions and the bytes. That is
not a gap; it is where an optional feature belongs.

So there are two routes and neither needs the engine changed. The first is the one this port takes next: chunks and
embeddings as rows, loaded into the in-process index at startup, which removes extraction and embedding from a restart -
the two costs that actually matter - while a flat index rebuild is linear and cheap. The second is to let the module own
the index through `CREATE VECTOR INDEX` and load the persisted one, which also removes the index build. It can be adopted
without giving up this port fusion, because the module supplies the vector leg candidates and the envelope still records
the ranking this port computed.

The module also carries `Fusion/ReciprocalRankFusion.cs` and `Fusion/PoolMerge.cs`. This port fuses by rank itself, for a
reason that still holds - the envelope records the ranking that decided the answer - so that stays as it is. It is noted
here as a capability that exists rather than as a change to make.