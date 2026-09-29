using AI.SemanticSearch.Api;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddExternalConfig(builder.Environment.ContentRootPath);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddOptions<EmbeddingOptions>().BindConfiguration(EmbeddingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SearchOptions>().BindConfiguration(SearchOptions.SectionName).ValidateDataAnnotations()
    .Validate(value => Math.Abs(value.VectorWeight + value.TextWeight - 1.0) < 0.000001, "Search weights must sum to 1.")
    .Validate(value => value.CandidateLimit >= value.MaximumLimit, "Search:CandidateLimit must be greater than or equal to Search:MaximumLimit.")
    .ValidateOnStart();
builder.Services.AddSingleton<IQueryEmbeddingProvider, QueryEmbeddingProvider>();
builder.Services.AddSingleton(_ => { var cs = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("ConnectionStrings:PostgreSql is required."); var b = new NpgsqlDataSourceBuilder(cs); b.UseVector(); return b.Build(); });
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("AI.SemanticSearchService"))
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation()
        .AddSource(SearchTelemetry.ActivitySourceName).AddSource("Npgsql"))
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddMeter(SearchTelemetry.MeterName));
var app = builder.Build();
_ = app.Services.GetRequiredService<IQueryEmbeddingProvider>();
app.UseExceptionHandler(); app.UseSwagger(); app.UseSwaggerUI();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = registration => registration.Tags.Contains("ready") });
app.MapPost("/search", async ([FromBody] SearchRequest request, HttpRequest httpRequest,
    ISearchService service, CancellationToken token) =>
{
    var scope = SearchScope.Create(httpRequest.Headers["X-Tenant-Id"].ToString(), httpRequest.Headers["X-Owner-Id"].ToString());
    return Results.Ok(await service.SearchAsync(request, scope, token));
}).AddEndpointFilter<RequestValidationFilter<SearchRequest>>()
    .WithName("SemanticSearch").Produces<SearchResponse>()
    .ProducesProblem(400).ProducesProblem(504).ProducesProblem(500);
app.Run();
public partial class Program;
