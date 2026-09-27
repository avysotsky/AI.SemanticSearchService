# Architecture

`POST /search` validates a bounded request, creates a local `query: ` multilingual-e5-base embedding, and runs one read-only PostgreSQL query against the ingestion service's `Ready` documents and `document_chunks`.

Ranking is deterministic: `0.75 * cosine similarity + 0.25 * normalized PostgreSQL simple-dictionary full-text rank`; document, sequence, and chunk identifiers break ties. The API never migrates or writes the shared schema. Every hit carries document/chunk/sequence/optional-page/file citation data.

Interfaces isolate embedding and search implementations. A later .NET 10 move only changes target frameworks and package baselines.

