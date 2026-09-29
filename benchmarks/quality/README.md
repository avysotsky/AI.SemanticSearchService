# Hybrid-search quality benchmark

`dataset.json` is a reproducible controlled factual corpus: 30 short TXT documents about SI base units, named derived units, and the quetta prefix, with one independently phrased question per document. The facts and wording were authored for this benchmark from the BIPM *SI Brochure*, 9th edition, version 3.01 (2025), DOI [10.59161/AUEZ1291](https://doi.org/10.59161/AUEZ1291). This is not a human-reviewed relevance set; judgments are deterministic document/chunk targets and each document is deliberately short enough to ingest as sequence `0`.

The corpus is designed to exercise retrieval and the complete ingestion path, not to estimate performance on production documents. Every document repeats its provenance URI and the dataset records its provenance and judgment method.

Run API + Worker + Semantic Search against an isolated benchmark database, then:

```powershell
./run.ps1 -Dataset ./dataset.json -IngestionBaseUrl http://localhost:5000 -SearchBaseUrl http://localhost:5102 -TenantId benchmark-si-v1 -OwnerId benchmark-runner
```

The PowerShell 5.1-compatible harness uploads 30–50 documents, waits for every document to become `Ready`, sends each question, and writes both `results.json` and an ignored timestamped copy. Results contain the actual document-id mapping, top-five chunk citations, macro chunk-level Recall@5, MRR, p50/p95 latency, and per-question recall/rank. Empty expected sets, duplicate keys, unknown documents, invalid sequences, missing files, non-Ready ingestion, and incomplete search responses fail the run.
