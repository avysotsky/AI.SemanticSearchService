# Architecture

`POST /search` validates a bounded request and trusted tenant/owner scope, creates a local `query: ` multilingual-e5-base embedding, and queries the ingestion-owned PostgreSQL schema read-only.

## Ranking

Two independent bounded candidate sets are produced:

1. pgvector cosine HNSW candidates ordered by distance;
2. PostgreSQL `simple`-dictionary FTS candidates ordered by `ts_rank_cd`.

Weighted reciprocal-rank fusion uses `weight / (k + rank)`. The result is divided by the theoretical rank-1 contribution of both sources, producing a stable `[0,1]` score. Missing membership contributes zero. Final ties use best source rank, document id, chunk sequence, and chunk id.

Both branches contain identical `Ready`, tenant, owner, document, content type, upload date, and JSONB metadata predicates. Failed, in-progress, unassigned legacy, and foreign-scope documents cannot enter fusion. Every hit includes a citation suitable for downstream RAG context attribution.

## Ownership and observability

`AI.DocumentIngestionService` exclusively owns migrations and writes. Its migration adds ownership/metadata columns, the `vector(768)` cosine HNSW index, FTS expression GIN index, and scope/status/date index. Semantic Search performs no schema mutation.

OpenTelemetry `ActivitySource`/`Meter` instrumentation records request, candidate, result, inference queue, active inference, and latency signals without query text. ONNX work is bounded by a semaphore. ASP.NET cancellation and PostgreSQL command timeout are propagated and mapped to non-success responses.

The scope headers are a trust boundary, not authentication. Production ingress must derive them from authenticated claims and strip external values.
