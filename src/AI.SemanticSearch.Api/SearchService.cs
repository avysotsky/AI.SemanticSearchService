using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace AI.SemanticSearch.Api;

public interface ISearchService
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}

public sealed class SearchService(IQueryEmbeddingProvider embeddingProvider, NpgsqlDataSource dataSource,
    IOptions<SearchOptions> options, ILogger<SearchService> logger) : ISearchService
{
    private const string Sql = """
        WITH scored AS (
            SELECT c.id chunk_id, c.document_id, c.sequence, c.page_number, c.text, d.file_name,
                   GREATEST(0.0, LEAST(1.0, 1.0 - (c.embedding <=> @embedding))) vector_score,
                   ts_rank_cd(to_tsvector('simple', c.text), websearch_to_tsquery('simple', @query), 32) text_score
            FROM document_chunks c JOIN documents d ON d.id = c.document_id
            WHERE d.status = 'Ready' AND (@document_id IS NULL OR d.id = @document_id)
        ), ranked AS (
            SELECT *, (@vector_weight * vector_score) + (@text_weight * text_score) score FROM scored
        )
        SELECT chunk_id, document_id, sequence, page_number, text, file_name, score, vector_score, text_score
        FROM ranked WHERE score >= @min_score
        ORDER BY score DESC, document_id, sequence, chunk_id LIMIT @limit;
        """;

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var query = request.Query.Trim();
        var limit = request.Limit ?? settings.DefaultLimit;
        if (limit > settings.MaximumLimit) throw new ArgumentOutOfRangeException(nameof(request), $"Limit cannot exceed {settings.MaximumLimit}.");
        var embedding = await embeddingProvider.EmbedAsync(query, cancellationToken);
        var hits = new List<SearchHit>(limit);
        await using var command = dataSource.CreateCommand(Sql);
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("embedding", new Vector(embedding));
        command.Parameters.AddWithValue("query", query);
        command.Parameters.Add(new NpgsqlParameter("document_id", NpgsqlDbType.Uuid)
        {
            Value = request.DocumentId is null ? DBNull.Value : request.DocumentId.Value,
        });
        command.Parameters.AddWithValue("vector_weight", settings.VectorWeight);
        command.Parameters.AddWithValue("text_weight", settings.TextWeight);
        command.Parameters.AddWithValue("min_score", request.MinScore);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            hits.Add(new SearchHit(reader.GetString(4), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8),
                new Citation(reader.GetGuid(1), reader.GetGuid(0), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetString(5))));
        logger.LogInformation("Semantic search returned {HitCount} hits for document filter {DocumentId}", hits.Count, request.DocumentId);
        return new SearchResponse(query, hits.Count, hits);
    }
}
