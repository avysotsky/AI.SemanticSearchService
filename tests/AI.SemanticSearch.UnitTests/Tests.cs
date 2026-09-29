using System.ComponentModel.DataAnnotations;
using AI.SemanticSearch.Api;
using Xunit;

namespace AI.SemanticSearch.UnitTests;

public sealed class Tests
{
    [Fact]
    public void MeanPoolAndNormalizeReturnsUnitVector()
    {
        var values = new float[2 * EmbeddingOptions.Dimensions]; values[0] = 2; values[EmbeddingOptions.Dimensions] = 2;
        var result = QueryEmbeddingProvider.MeanPoolAndNormalize(values, 2);
        Assert.Equal(1f, result[0], 5); Assert.Equal(1d, Math.Sqrt(result.Sum(value => value * value)), 5);
    }

    [Fact]
    public void MeanPoolAndNormalizeRejectsWrongShape() =>
        Assert.Throws<ArgumentException>(() => QueryEmbeddingProvider.MeanPoolAndNormalize([1f], 1));

    [Fact]
    public void SearchRequestRejectsInvalidValues()
    {
        var request = new SearchRequest("x", Limit: 101, MinScore: 2); var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), results, true)); Assert.Equal(3, results.Count);
    }

    [Fact]
    public void RrfScoreIsNormalizedAndRewardsAgreement()
    {
        var bothFirst = SearchService.NormalizeRrf(1, 1, 60, 0.5, 0.5);
        var vectorOnly = SearchService.NormalizeRrf(1, null, 60, 0.5, 0.5);
        Assert.Equal(1, bothFirst, 12);
        Assert.InRange(vectorOnly, 0, 1);
        Assert.True(bothFirst > vectorOnly);
    }

    [Theory]
    [InlineData("", "owner")]
    [InlineData("tenant", "")]
    [InlineData("__legacy_unassigned__", "owner")]
    public void ScopeRejectsMissingOrReservedIdentifiers(string tenant, string owner) =>
        Assert.Throws<ArgumentException>(() => SearchScope.Create(tenant, owner));
}
