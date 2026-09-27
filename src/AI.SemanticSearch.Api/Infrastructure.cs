using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AI.SemanticSearch.Api;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception is ArgumentException ? 400 : 500;
        if (status == 500) logger.LogError(exception, "Unhandled semantic search error");
        await Results.Problem(statusCode: status, title: status == 400 ? "Invalid search request" : "Semantic search failed",
            detail: status == 400 ? exception.Message : null).ExecuteAsync(httpContext);
        return true;
    }
}

public sealed class PostgreSqlHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1 FROM documents d WHERE d.status = 'Ready' LIMIT 1;");
            command.CommandTimeout = 3;
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        { return HealthCheckResult.Unhealthy("Shared PostgreSQL schema is unavailable.", exception); }
    }
}
