using System.Text.Json;
using AI.SemanticSearch.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using Xunit;

namespace AI.SemanticSearch.IntegrationTests;

public sealed class PostgreSqlHybridSearchTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PgvectorAndFtsEnforceScopeStatusFiltersAndStableParallelSearch()
    {
        var configured = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(configured)) return;

        var schema = "semantic_search_test_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlDataSourceBuilder(configured);
        adminBuilder.UseVector();
        await using var admin = adminBuilder.Build();
        await using (var command = admin.CreateCommand($"CREATE SCHEMA {schema};")) await command.ExecuteNonQueryAsync();

        try
        {
            var scopedConnection = new NpgsqlConnectionStringBuilder(configured) { SearchPath = $"{schema},public" }.ConnectionString;
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(scopedConnection);
            dataSourceBuilder.UseVector();
            await using var dataSource = dataSourceBuilder.Build();
            await CreateSchemaAsync(dataSource);
            var seeded = await SeedAsync(dataSource);

            var options = Options.Create(new SearchOptions
            {
                DefaultLimit = 5, MaximumLimit = 20, CandidateLimit = 20, RrfK = 60,
                VectorWeight = 0.5, TextWeight = 0.5, CommandTimeoutSeconds = 5,
            });
            var service = new SearchService(new FixedEmbeddingProvider(VectorAt(0)), dataSource, options,
                NullLogger<SearchService>.Instance);
            using var metadata = JsonDocument.Parse("{\"department\":\"legal\"}");
            var request = new SearchRequest("Which clause covers alpha termination obligations?", DocumentType: "text/plain",
                Metadata: new Dictionary<string, JsonElement> { ["department"] = metadata.RootElement.GetProperty("department").Clone() });
            var scope = SearchScope.Create("tenant-a", "owner-a");

            var first = await service.SearchAsync(request, scope, CancellationToken.None);
            var second = await service.SearchAsync(request, scope, CancellationToken.None);
            Assert.Single(first.Results);
            Assert.Equal(request.Query, first.Query);
            Assert.Equal(first.Results.Select(x => x.Citation.ChunkId), second.Results.Select(x => x.Citation.ChunkId));
            Assert.All(first.Results, hit => Assert.InRange(hit.Score, 0, 1));
            Assert.True(first.Results[0].TextScore > 0, "Natural-language questions must produce FTS candidates even when not every query token occurs in the chunk.");

            var documentMatch = await service.SearchAsync(request with { DocumentId = seeded.DocumentId }, scope, CancellationToken.None);
            Assert.Single(documentMatch.Results);
            Assert.Equal(seeded.DocumentId, documentMatch.Results[0].Citation.DocumentId);
            Assert.Empty((await service.SearchAsync(request with { DocumentId = Guid.NewGuid() }, scope, CancellationToken.None)).Results);

            Assert.Single((await service.SearchAsync(request with { UploadedFrom = seeded.CreatedAt.AddSeconds(-1) }, scope, CancellationToken.None)).Results);
            Assert.Empty((await service.SearchAsync(request with { UploadedFrom = seeded.CreatedAt.AddSeconds(1) }, scope, CancellationToken.None)).Results);
            Assert.Single((await service.SearchAsync(request with { UploadedTo = seeded.CreatedAt.AddSeconds(1) }, scope, CancellationToken.None)).Results);
            Assert.Empty((await service.SearchAsync(request with { UploadedTo = seeded.CreatedAt.AddSeconds(-1) }, scope, CancellationToken.None)).Results);

            var parallel = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => service.SearchAsync(request, scope, CancellationToken.None)));
            Assert.All(parallel, response => Assert.Single(response.Results));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync(request, scope, cancelled.Token));

            await using (var blocker = await dataSource.OpenConnectionAsync())
            await using (var transaction = await blocker.BeginTransactionAsync())
            {
                await using var lockCommand = new NpgsqlCommand("LOCK TABLE document_chunks IN ACCESS EXCLUSIVE MODE;", blocker, transaction);
                await lockCommand.ExecuteNonQueryAsync();
                var timeoutOptions = Options.Create(new SearchOptions
                {
                    DefaultLimit = 5, MaximumLimit = 20, CandidateLimit = 20, RrfK = 60,
                    VectorWeight = 0.5, TextWeight = 0.5, CommandTimeoutSeconds = 1,
                });
                var timeoutService = new SearchService(new FixedEmbeddingProvider(VectorAt(0)), dataSource,
                    timeoutOptions, NullLogger<SearchService>.Instance);
                await Assert.ThrowsAnyAsync<NpgsqlException>(() =>
                    timeoutService.SearchAsync(request, scope, CancellationToken.None));
                await transaction.RollbackAsync();
            }

            await using var indexCommand = dataSource.CreateCommand("SELECT COUNT(*) FROM pg_indexes WHERE schemaname = current_schema() AND indexname IN ('ix_chunks_hnsw', 'ix_chunks_fts');");
            Assert.Equal(2L, (long)(await indexCommand.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var cleanup = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE;");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static string? ResolveConnectionString()
    {
        var direct = Environment.GetEnvironmentVariable("SEMANTIC_SEARCH_TEST_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        var configPath = Environment.GetEnvironmentVariable("SEMANTIC_SEARCH_TEST_CONFIG_PATH");
        if (string.IsNullOrWhiteSpace(configPath)) return null;
        if (!File.Exists(configPath))
            throw new InvalidOperationException($"SEMANTIC_SEARCH_TEST_CONFIG_PATH does not exist: {configPath}");
        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        return config.RootElement.GetProperty("ConnectionStrings").GetProperty("PostgreSql").GetString();
    }

    private static async Task CreateSchemaAsync(NpgsqlDataSource dataSource)
    {
        const string sql = """
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE TABLE documents (
              id uuid PRIMARY KEY, file_name varchar(255) NOT NULL, content_type varchar(100) NOT NULL,
              status varchar(32) NOT NULL, created_at timestamptz NOT NULL,
              tenant_id varchar(128) NOT NULL, owner_id varchar(128) NOT NULL, metadata jsonb NOT NULL DEFAULT '{}');
            CREATE TABLE document_chunks (
              id uuid PRIMARY KEY, document_id uuid NOT NULL REFERENCES documents(id), sequence int NOT NULL,
              page_number int NULL, text text NOT NULL, embedding vector(768) NOT NULL);
            CREATE INDEX ix_chunks_hnsw ON document_chunks USING hnsw (embedding vector_cosine_ops);
            CREATE INDEX ix_chunks_fts ON document_chunks USING gin (to_tsvector('simple', text));
            """;
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(Guid DocumentId, DateTimeOffset CreatedAt)> SeedAsync(NpgsqlDataSource dataSource)
    {
        var ownedReadyId = Guid.NewGuid();
        var ownedReadyCreatedAt = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            (ownedReadyId, ownedReadyCreatedAt, "Ready", "tenant-a", "owner-a", "alpha termination clause", "{\"department\":\"legal\"}"),
            (Guid.NewGuid(), ownedReadyCreatedAt, "Failed", "tenant-a", "owner-a", "alpha failed secret", "{\"department\":\"legal\"}"),
            (Guid.NewGuid(), ownedReadyCreatedAt, "Ready", "tenant-a", "owner-b", "alpha other owner secret", "{\"department\":\"legal\"}"),
            (Guid.NewGuid(), ownedReadyCreatedAt, "Ready", "tenant-b", "owner-a", "alpha other tenant secret", "{\"department\":\"legal\"}"),
        };
        foreach (var row in rows)
        {
            await using var command = dataSource.CreateCommand("""
                INSERT INTO documents(id,file_name,content_type,status,created_at,tenant_id,owner_id,metadata)
                VALUES (@id,'contract.txt','text/plain',@status,@created_at,@tenant,@owner,@metadata::jsonb);
                INSERT INTO document_chunks(id,document_id,sequence,page_number,text,embedding)
                VALUES (@chunk,@id,0,1,@text,@embedding);
                """);
            command.Parameters.AddWithValue("id", row.Item1);
            command.Parameters.AddWithValue("chunk", Guid.NewGuid());
            command.Parameters.AddWithValue("created_at", row.Item2);
            command.Parameters.AddWithValue("status", row.Item3);
            command.Parameters.AddWithValue("tenant", row.Item4);
            command.Parameters.AddWithValue("owner", row.Item5);
            command.Parameters.AddWithValue("text", row.Item6);
            command.Parameters.AddWithValue("metadata", row.Item7);
            command.Parameters.AddWithValue("embedding", new Vector(VectorAt(0)));
            await command.ExecuteNonQueryAsync();
        }

        return (ownedReadyId, ownedReadyCreatedAt);
    }

    private static float[] VectorAt(int index)
    {
        var vector = new float[EmbeddingOptions.Dimensions];
        vector[index] = 1;
        return vector;
    }

    private sealed class FixedEmbeddingProvider(float[] embedding) : IQueryEmbeddingProvider
    {
        public Task<float[]> EmbedAsync(string query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(embedding);
        }
    }
}
