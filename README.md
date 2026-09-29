# AI Semantic Search Service

.NET 8 read-only hybrid-search API over `Ready` chunks produced by `AI.DocumentIngestionService`. Query embeddings use local `intfloat/multilingual-e5-base` ONNX inference; PostgreSQL provides pgvector HNSW and language-neutral `simple` FTS. No paid cloud is used.

Flow: `upload -> Ready -> indexed chunks -> vector + FTS candidate sets -> weighted RRF -> citations -> RAG consumer`.

## Search contract

`POST /search` requires trusted `X-Tenant-Id` and `X-Owner-Id` headers. Deploy behind authentication middleware/gateway that removes caller-supplied scope headers and injects verified claim values; this repository does not contain an identity provider. Legacy rows migrated without ownership are assigned the reserved `__legacy_unassigned__` scope and are deliberately not searchable.

```json
{
  "query": "termination conditions",
  "documentId": null,
  "documentType": "application/pdf",
  "uploadedFrom": "2026-01-01T00:00:00Z",
  "uploadedTo": "2026-12-31T23:59:59Z",
  "metadata": {"department":"legal"},
  "limit": 5,
  "minScore": 0.2
}
```

All filters, `status = 'Ready'`, tenant, and owner constraints are applied independently to both candidate queries. Metadata uses JSONB containment. Results carry document/chunk/sequence/page/file citations and a normalized `[0,1]` weighted-RRF score. Sorting is stable: score, best source rank, document, sequence, chunk.

## Run

Copy `config.example.json` to ignored `config.json`, or set `SEMANTIC_SEARCH_CONFIG_PATH`. Keep credentials and the external model outside Git.

```powershell
Copy-Item config.example.json config.json
dotnet run --project src/AI.SemanticSearch.Api --urls http://localhost:5102
```

Endpoints: `POST /search`, `GET /health/live`, `GET /health/ready`, `GET /swagger`.

```powershell
$headers = @{'X-Tenant-Id'='tenant-a';'X-Owner-Id'='owner-a'}
Invoke-RestMethod http://localhost:5102/search -Method Post -Headers $headers -ContentType application/json -Body (@{query='termination conditions';limit=5}|ConvertTo-Json)
```

## Operations and verification

- ONNX concurrency is bounded by `Embeddings:MaximumConcurrency`; queue and inference durations are measured.
- OpenTelemetry sources expose ASP.NET spans/metrics plus `search.duration`, candidate counts, result counts, inference queue/duration, and active inference. Attach an exporter through deployment auto-instrumentation/collector configuration.
- Structured logs contain counts and timings, never query text or result text.
- PostgreSQL timeout is bounded by `Search:CommandTimeoutSeconds`; request cancellation flows to inference and Npgsql.
- The ingestion-owned migration must be applied before readiness succeeds.

```powershell
dotnet build --configuration Release
dotnet test --configuration Release --no-build
$env:SEMANTIC_SEARCH_TEST_CONFIG_PATH='D:\path\to\isolated-test-config.json'
dotnet test tests/AI.SemanticSearch.IntegrationTests --configuration Release
```

Integration tests are inert unless the explicit test connection variable is present. They create/drop only a randomized schema and cover pgvector/FTS indexes, Ready-only tenant/owner isolation, filters, deterministic ranking, cancellation, and parallel requests. The reproducible 30-document factual quality corpus, harness, provenance, judgments, and measured baseline are in `benchmarks/quality/`.
