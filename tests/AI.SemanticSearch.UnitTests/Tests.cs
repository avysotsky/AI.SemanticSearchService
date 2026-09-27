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
}
