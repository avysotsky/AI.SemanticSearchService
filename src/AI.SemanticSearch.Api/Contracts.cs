using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace AI.SemanticSearch.Api;

public sealed record SearchRequest(
    [property: Required, StringLength(2000, MinimumLength = 2)] string Query,
    Guid? DocumentId = null,
    [property: StringLength(100)] string? DocumentType = null,
    DateTimeOffset? UploadedFrom = null,
    DateTimeOffset? UploadedTo = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: Range(1, 100)] int? Limit = null,
    [property: Range(0, 1)] double MinScore = 0);

public sealed record SearchScope(string TenantId, string OwnerId)
{
    public static SearchScope Create(string tenantId, string ownerId)
    {
        tenantId = Validate(tenantId, nameof(tenantId));
        ownerId = Validate(ownerId, nameof(ownerId));
        return new SearchScope(tenantId, ownerId);
    }

    private static string Validate(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.StartsWith("__", StringComparison.Ordinal))
            throw new ArgumentException("A valid trusted scope identifier is required.", name);
        return value.Trim();
    }
}

public sealed record Citation(Guid DocumentId, Guid ChunkId, int Sequence, int? Page, string FileName);
public sealed record SearchHit(string Text, double Score, double VectorScore, double TextScore, Citation Citation);
public sealed record SearchResponse(string Query, int Count, IReadOnlyList<SearchHit> Results);
