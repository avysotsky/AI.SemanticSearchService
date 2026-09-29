using System.ComponentModel.DataAnnotations;

namespace AI.SemanticSearch.Api;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";
    public const int Dimensions = 768;
    [Required] public string ModelPath { get; init; } = string.Empty;
    [Range(8, 512)] public int MaxTokenLength { get; init; } = 512;
    [Range(1, 16)] public int MaximumConcurrency { get; init; } = 1;
}

public sealed class SearchOptions
{
    public const string SectionName = "Search";
    [Range(1, 50)] public int DefaultLimit { get; init; } = 10;
    [Range(1, 100)] public int MaximumLimit { get; init; } = 50;
    [Range(1, 120)] public int CommandTimeoutSeconds { get; init; } = 15;
    [Range(5, 1000)] public int CandidateLimit { get; init; } = 100;
    [Range(1, 1000)] public int RrfK { get; init; } = 60;
    [Range(0, 1)] public double VectorWeight { get; init; } = 0.5;
    [Range(0, 1)] public double TextWeight { get; init; } = 0.5;
}

public static class ExternalConfiguration
{
    public const string EnvironmentVariable = "SEMANTIC_SEARCH_CONFIG_PATH";

    public static IConfigurationBuilder AddExternalConfig(this IConfigurationBuilder builder, string startDirectory)
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return builder.AddJsonFile(Path.GetFullPath(configured, startDirectory), false, true);

        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "config.json");
            if (File.Exists(candidate)) return builder.AddJsonFile(candidate, false, true);
        }

        throw new FileNotFoundException($"External config.json was not found. Set {EnvironmentVariable}.");
    }
}

