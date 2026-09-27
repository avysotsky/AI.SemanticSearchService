using AI.SemanticSearch.Api;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddExternalConfig(builder.Environment.ContentRootPath);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddOptions<EmbeddingOptions>().BindConfiguration(EmbeddingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SearchOptions>().BindConfiguration(SearchOptions.SectionName).ValidateDataAnnotations()
    .Validate(value => Math.Abs(value.VectorWeight + value.TextWeight - 1.0) < 0.000001, "Search weights must sum to 1.").ValidateOnStart();
builder.Services.AddSingleton<IQueryEmbeddingProvider, QueryEmbeddingProvider>();
builder.Services.AddSingleton(_ => { var cs = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("ConnectionStrings:PostgreSql is required."); var b = new NpgsqlDataSourceBuilder(cs); b.UseVector(); return b.Build(); });
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"]);
var app = builder.Build();
_ = app.Services.GetRequiredService<IQueryEmbeddingProvider>();
app.UseExceptionHandler(); app.UseSwagger(); app.UseSwaggerUI();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = registration => registration.Tags.Contains("ready") });
app.MapPost("/search", async ([FromBody] SearchRequest request, ISearchService service, CancellationToken token) =>
    Results.Ok(await service.SearchAsync(request, token))).AddEndpointFilter<RequestValidationFilter<SearchRequest>>()
    .WithName("SemanticSearch").Produces<SearchResponse>()
    .ProducesProblem(400).ProducesProblem(500);
app.Run();
public partial class Program;
