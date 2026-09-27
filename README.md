# AI Semantic Search Service

.NET 8 REST MVP for hybrid search over `Ready` documents produced by `AI.DocumentIngestionService`. It uses the same local `intfloat/multilingual-e5-base` ONNX model, `query: ` prefix, mean pooling, L2 normalization, PostgreSQL full text, and pgvector cosine distance. No paid cloud is used.

Flow: `Document Ingestion (1) -> shared PostgreSQL/pgvector -> Semantic Search (2) -> RAG Answer (3)`. The database access is read-only. See [architecture](docs/architecture.md).

## Run

Copy `config.example.json` to ignored `config.json` and replace placeholders, or set `SEMANTIC_SEARCH_CONFIG_PATH`. Standard .NET environment keys are supported. Keep secrets outside Git. The model remains external at `D:\AI.Models\multilingual-e5-base`.

```powershell
Copy-Item config.example.json config.json
dotnet run --project src/AI.SemanticSearch.Api --urls http://localhost:5102
```

Endpoints: `POST /search`, `GET /health/live`, `GET /health/ready`, `GET /swagger`.

```bash
curl -X POST http://localhost:5102/search -H "Content-Type: application/json" -d '{"query":"termination conditions","limit":5,"minScore":0.2}'
```

```powershell
Invoke-RestMethod http://localhost:5102/search -Method Post -ContentType application/json -Body (@{query='termination conditions';limit=5;minScore=0.2}|ConvertTo-Json)
```

Verify: `dotnet build --configuration Release`, then `dotnet test --configuration Release --no-build`.

## Limits

- PostgreSQL `simple` FTS is language-neutral.
- Search scans eligible chunks until an ANN/FTS index migration is approved for the shared database; this service intentionally performs no migrations.
- ONNX inference is serialized because the tokenizer wrapper is not guaranteed thread-safe.

