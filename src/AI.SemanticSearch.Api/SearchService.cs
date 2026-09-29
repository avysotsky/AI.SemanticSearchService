using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace AI.SemanticSearch.Api;

public interface ISearchService
{
    Task<SearchResponse> SearchAsync(SearchRequest request, SearchScope scope, CancellationToken cancellationToken);
}

public sealed class SearchService(IQueryEmbeddingProvider embeddingProvider, NpgsqlDataSource dataSource,
    IOptions<SearchOptions> options, ILogger<SearchService> logger) : ISearchService
{
    private const string Sql = """
        WITH lexical_query AS MATERIALIZED (
            SELECT CASE
                     WHEN COUNT(*) = 0 THEN NULL::tsquery
                     ELSE to_tsquery('simple', string_agg(quote_literal(token), ' | ' ORDER BY token))
                   END value
            FROM unnest(tsvector_to_array(to_tsvector('simple', @query))) token
        ), vector_candidates AS MATERIALIZED (
            SELECT c.id chunk_id, c.document_id, c.sequence, c.page_number, c.text, d.file_name,
                   GREATEST(0.0, LEAST(1.0, 1.0 - (c.embedding <=> @embedding))) vector_score,
                   ROW_NUMBER() OVER (ORDER BY c.embedding <=> @embedding, d.id, c.sequence, c.id) vector_rank
            FROM document_chunks c
            JOIN documents d ON d.id = c.document_id
            WHERE d.status = 'Ready'
              AND d.tenant_id = @tenant_id AND d.owner_id = @owner_id
              AND (@document_id IS NULL OR d.id = @document_id)
              AND (@document_type IS NULL OR d.content_type = @document_type)
              AND (@uploaded_from IS NULL OR d.created_at >= @uploaded_from)
              AND (@uploaded_to IS NULL OR d.created_at <= @uploaded_to)
              AND (@metadata IS NULL OR d.metadata @> @metadata)
            ORDER BY c.embedding <=> @embedding, d.id, c.sequence, c.id
            LIMIT @candidate_limit
        ), text_scored AS MATERIALIZED (
            SELECT c.id chunk_id, c.document_id, c.sequence, c.page_number, c.text, d.file_name,
                   ts_rank_cd(to_tsvector('simple', c.text), q.value, 32) text_score
            FROM document_chunks c
            JOIN documents d ON d.id = c.document_id
            CROSS JOIN lexical_query q
            WHERE d.status = 'Ready'
              AND d.tenant_id = @tenant_id AND d.owner_id = @owner_id
              AND (@document_id IS NULL OR d.id = @document_id)
              AND (@document_type IS NULL OR d.content_type = @document_type)
              AND (@uploaded_from IS NULL OR d.created_at >= @uploaded_from)
              AND (@uploaded_to IS NULL OR d.created_at <= @uploaded_to)
              AND (@metadata IS NULL OR d.metadata @> @metadata)
              AND q.value IS NOT NULL
              AND to_tsvector('simple', c.text) @@ q.value
        ), text_candidates AS MATERIALIZED (
            SELECT *, ROW_NUMBER() OVER (ORDER BY text_score DESC, document_id, sequence, chunk_id) text_rank
            FROM text_scored
            ORDER BY text_score DESC, document_id, sequence, chunk_id
            LIMIT @candidate_limit
        ), candidate_ids AS (
            SELECT chunk_id FROM vector_candidates
            UNION
            SELECT chunk_id FROM text_candidates
        ), fused AS (
            SELECT COALESCE(v.chunk_id, t.chunk_id) chunk_id,
                   COALESCE(v.document_id, t.document_id) document_id,
                   COALESCE(v.sequence, t.sequence) sequence,
                   COALESCE(v.page_number, t.page_number) page_number,
                   COALESCE(v.text, t.text) text,
                   COALESCE(v.file_name, t.file_name) file_name,
                   COALESCE(v.vector_score, 0.0) vector_score,
                   COALESCE(t.text_score, 0.0) text_score,
                   v.vector_rank, t.text_rank,
                   ((CASE WHEN v.vector_rank IS NULL THEN 0.0 ELSE @vector_weight / (@rrf_k + v.vector_rank) END) +
                    (CASE WHEN t.text_rank IS NULL THEN 0.0 ELSE @text_weight / (@rrf_k + t.text_rank) END)) /
                    ((@vector_weight + @text_weight) / (@rrf_k + 1.0)) score
            FROM candidate_ids i
            LEFT JOIN vector_candidates v ON v.chunk_id = i.chunk_id
            LEFT JOIN text_candidates t ON t.chunk_id = i.chunk_id
        )
        SELECT chunk_id, document_id, sequence, page_number, text, file_name,
               score, vector_score, text_score, vector_rank, text_rank,
               (SELECT COUNT(*) FROM vector_candidates), (SELECT COUNT(*) FROM text_candidates)
        FROM fused
        ORDER BY score DESC,
                 LEAST(COALESCE(vector_rank, @candidate_limit + 1), COALESCE(text_rank, @candidate_limit + 1)),
                 document_id, sequence, chunk_id;
        """;

    public async Task<SearchResponse> SearchAsync(
        SearchRequest request,
        SearchScope scope,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        Validate(request, settings);
        var query = request.Query.Trim();
        using var activity = SearchTelemetry.Activities.StartActivity("hybrid-search");
        activity?.SetTag("search.document_filter", request.DocumentId.HasValue);
        activity?.SetTag("search.metadata_filter", request.Metadata is not null);
        var timer = Stopwatch.StartNew();

        try
        {
            var embedding = await embeddingProvider.EmbedAsync(query, cancellationToken);
            var rows = new List<RankedRow>(settings.CandidateLimit * 2);
            long vectorCandidates = 0;
            long textCandidates = 0;

            await using var command = dataSource.CreateCommand(Sql);
            command.CommandTimeout = settings.CommandTimeoutSeconds;
            command.Parameters.AddWithValue("embedding", new Vector(embedding));
            command.Parameters.AddWithValue("query", query);
            AddNullable(command, "document_id", NpgsqlDbType.Uuid, request.DocumentId);
            AddNullable(command, "document_type", NpgsqlDbType.Varchar, request.DocumentType?.Trim());
            AddNullable(command, "uploaded_from", NpgsqlDbType.TimestampTz, request.UploadedFrom);
            AddNullable(command, "uploaded_to", NpgsqlDbType.TimestampTz, request.UploadedTo);
            AddNullable(command, "metadata", NpgsqlDbType.Jsonb,
                request.Metadata is null ? null : JsonSerializer.Serialize(request.Metadata));
            command.Parameters.AddWithValue("tenant_id", scope.TenantId);
            command.Parameters.AddWithValue("owner_id", scope.OwnerId);
            command.Parameters.AddWithValue("candidate_limit", settings.CandidateLimit);
            command.Parameters.AddWithValue("rrf_k", settings.RrfK);
            command.Parameters.AddWithValue("vector_weight", settings.VectorWeight);
            command.Parameters.AddWithValue("text_weight", settings.TextWeight);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                vectorCandidates = reader.GetInt64(11);
                textCandidates = reader.GetInt64(12);
                rows.Add(new RankedRow(
                    new SearchHit(reader.GetString(4), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8),
                        new Citation(reader.GetGuid(1), reader.GetGuid(0), reader.GetInt32(2),
                            reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetString(5)))));
            }

            var limit = request.Limit ?? settings.DefaultLimit;
            var hits = rows.Select(row => row.Hit).Where(hit => hit.Score >= request.MinScore).Take(limit).ToArray();
            SearchTelemetry.VectorCandidates.Record(vectorCandidates);
            SearchTelemetry.TextCandidates.Record(textCandidates);
            SearchTelemetry.ResultCount.Record(hits.LongLength);
            activity?.SetTag("search.results", hits.Length);
            logger.LogInformation("Hybrid search completed with {HitCount} hits from {VectorCandidateCount} vector and {TextCandidateCount} FTS candidates",
                hits.Length, vectorCandidates, textCandidates);
            return new SearchResponse(query, hits.Length, hits);
        }
        finally
        {
            SearchTelemetry.SearchDuration.Record(timer.Elapsed.TotalMilliseconds);
        }
    }

    public static double NormalizeRrf(int? vectorRank, int? textRank, int k, double vectorWeight, double textWeight)
    {
        var raw = (vectorRank is null ? 0 : vectorWeight / (k + vectorRank.Value)) +
                  (textRank is null ? 0 : textWeight / (k + textRank.Value));
        return raw / ((vectorWeight + textWeight) / (k + 1.0));
    }

    private static void Validate(SearchRequest request, SearchOptions settings)
    {
        var limit = request.Limit ?? settings.DefaultLimit;
        if (limit > settings.MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(request), $"Limit cannot exceed {settings.MaximumLimit}.");
        if (request.UploadedFrom > request.UploadedTo)
            throw new ArgumentException("UploadedFrom cannot be later than UploadedTo.", nameof(request));
        if (request.Metadata is { Count: > 20 })
            throw new ArgumentException("Metadata filter cannot contain more than 20 keys.", nameof(request));
        if (request.Metadata is not null && JsonSerializer.Serialize(request.Metadata).Length > 8_192)
            throw new ArgumentException("Metadata filter is too large.", nameof(request));
    }

    private static void AddNullable<T>(NpgsqlCommand command, string name, NpgsqlDbType type, T? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value is null ? DBNull.Value : value });
    }

    private sealed record RankedRow(SearchHit Hit);
}
