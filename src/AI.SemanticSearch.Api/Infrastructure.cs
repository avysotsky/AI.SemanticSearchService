using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AI.SemanticSearch.Api;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            ArgumentException => StatusCodes.Status400BadRequest,
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => 499,
            PostgresException { SqlState: PostgresErrorCodes.QueryCanceled } => StatusCodes.Status504GatewayTimeout,
            TimeoutException => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError,
        };
        if (status >= 500) logger.LogError(exception, "Semantic search request failed with status {StatusCode}", status);
        await Results.Problem(statusCode: status,
            title: status == 400 ? "Invalid search request" : status == 504 ? "Semantic search timed out" : "Semantic search failed",
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
            await using var command = dataSource.CreateCommand("SELECT d.tenant_id, d.owner_id, d.metadata FROM documents d LIMIT 1;");
            command.CommandTimeout = 3;
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        { return HealthCheckResult.Unhealthy("Shared PostgreSQL schema is unavailable.", exception); }
    }
}
