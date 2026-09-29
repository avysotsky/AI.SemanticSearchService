using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.HuggingFace.Tokenizer;

namespace AI.SemanticSearch.Api;

public interface IQueryEmbeddingProvider
{
    Task<float[]> EmbedAsync(string query, CancellationToken cancellationToken);
}

public sealed class QueryEmbeddingProvider : IQueryEmbeddingProvider, IDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly Tokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly SemaphoreSlim _gate;
    private readonly object _tokenizerLock = new();

    public QueryEmbeddingProvider(IOptions<EmbeddingOptions> options)
    {
        _options = options.Value;
        _gate = new SemaphoreSlim(_options.MaximumConcurrency, _options.MaximumConcurrency);
        var root = Path.GetFullPath(_options.ModelPath);
        var modelPath = Path.Combine(root, "model.onnx");
        var tokenizerPath = Path.Combine(root, "tokenizer.json");
        if (!File.Exists(modelPath) || !File.Exists(tokenizerPath))
            throw new FileNotFoundException($"multilingual-e5-base model.onnx/tokenizer.json were not found in '{root}'.");

        _tokenizer = Tokenizer.FromFile(tokenizerPath);
        _session = new InferenceSession(modelPath);
        var output = _session.OutputMetadata.Values.FirstOrDefault();
        if (output is null || output.Dimensions.Length != 3 || output.Dimensions[^1] != EmbeddingOptions.Dimensions)
            throw new InvalidOperationException("The ONNX model must produce 768-dimensional token embeddings.");
    }

    public async Task<float[]> EmbedAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        IReadOnlyList<uint> tokenIds;
        lock (_tokenizerLock)
        {
            tokenIds = _tokenizer.Encode("query: " + query.Trim(), false).Encodings[0].Ids.ToArray();
        }

        var contentLength = Math.Min(tokenIds.Count, _options.MaxTokenLength - 2);
        var sequence = new long[contentLength + 2];
        sequence[0] = 0;
        for (var i = 0; i < contentLength; i++) sequence[i + 1] = tokenIds[i];
        sequence[^1] = 2;
        var ids = new DenseTensor<long>([1, sequence.Length]);
        var mask = new DenseTensor<long>([1, sequence.Length]);
        for (var i = 0; i < sequence.Length; i++) { ids[0, i] = sequence[i]; mask[0, i] = 1; }

        var queueTimer = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        SearchTelemetry.InferenceQueueDuration.Record(queueTimer.Elapsed.TotalMilliseconds);
        SearchTelemetry.ActiveInferences.Add(1);
        var inferenceTimer = Stopwatch.StartNew();
        try
        {
            using var results = _session.Run([
                NamedOnnxValue.CreateFromTensor("input_ids", ids),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            ]);
            return MeanPoolAndNormalize(results[0].AsTensor<float>().ToArray(), sequence.Length);
        }
        finally
        {
            SearchTelemetry.InferenceDuration.Record(inferenceTimer.Elapsed.TotalMilliseconds);
            SearchTelemetry.ActiveInferences.Add(-1);
            _gate.Release();
        }
    }

    public static float[] MeanPoolAndNormalize(ReadOnlySpan<float> values, int tokenCount)
    {
        if (values.Length != tokenCount * EmbeddingOptions.Dimensions)
            throw new ArgumentException("Unexpected embedding tensor size.", nameof(values));
        var vector = new float[EmbeddingOptions.Dimensions];
        for (var token = 0; token < tokenCount; token++)
            for (var dimension = 0; dimension < vector.Length; dimension++)
                vector[dimension] += values[(token * vector.Length) + dimension];
        double squaredNorm = 0;
        for (var i = 0; i < vector.Length; i++) { vector[i] /= tokenCount; squaredNorm += vector[i] * vector[i]; }
        var norm = Math.Sqrt(squaredNorm);
        if (norm <= 0) throw new InvalidOperationException("Model produced a zero-length embedding.");
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        return vector;
    }

    public void Dispose() { _session.Dispose(); _gate.Dispose(); }
}
