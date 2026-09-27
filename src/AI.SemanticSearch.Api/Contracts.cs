using System.ComponentModel.DataAnnotations;

namespace AI.SemanticSearch.Api;

public sealed record SearchRequest(
    [property: Required, StringLength(2000, MinimumLength = 2)] string Query,
    Guid? DocumentId = null,
    [property: Range(1, 100)] int? Limit = null,
    [property: Range(0, 1)] double MinScore = 0);

public sealed record Citation(Guid DocumentId, Guid ChunkId, int Sequence, int? Page, string FileName);
public sealed record SearchHit(string Text, double Score, double VectorScore, double TextScore, Citation Citation);
public sealed record SearchResponse(string Query, int Count, IReadOnlyList<SearchHit> Results);

