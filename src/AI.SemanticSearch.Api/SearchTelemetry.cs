using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AI.SemanticSearch.Api;

public static class SearchTelemetry
{
    public const string ActivitySourceName = "AI.SemanticSearch";
    public const string MeterName = "AI.SemanticSearch";
    public static readonly ActivitySource Activities = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Histogram<double> SearchDuration = Meter.CreateHistogram<double>("search.duration", "ms");
    public static readonly Histogram<double> InferenceDuration = Meter.CreateHistogram<double>("embedding.inference.duration", "ms");
    public static readonly Histogram<double> InferenceQueueDuration = Meter.CreateHistogram<double>("embedding.queue.duration", "ms");
    public static readonly UpDownCounter<long> ActiveInferences = Meter.CreateUpDownCounter<long>("embedding.active");
    public static readonly Histogram<long> VectorCandidates = Meter.CreateHistogram<long>("search.vector.candidates");
    public static readonly Histogram<long> TextCandidates = Meter.CreateHistogram<long>("search.fts.candidates");
    public static readonly Histogram<long> ResultCount = Meter.CreateHistogram<long>("search.results");
}
