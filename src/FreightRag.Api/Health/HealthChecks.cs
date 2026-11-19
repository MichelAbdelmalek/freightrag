using FreightRag.Core.Infrastructure.Configuration;
using FreightRag.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FreightRag.Api.Health;

/// <summary>Readiness: can EF Core connect to Postgres?</summary>
internal sealed class PostgresHealthCheck(IDbContextFactory<FreightRagDbContext> contexts) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Postgres is unreachable."); // no connection details in the output
    }
}

/// <summary>Readiness: does Ollama answer?</summary>
internal sealed class OllamaHealthCheck(IHttpClientFactory httpClients, FreightRagSettings settings) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync(new Uri(settings.Ollama.Url, "api/version"), cancellationToken);
            return response.IsSuccessStatusCode ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Ollama returned an error.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("Ollama is unreachable.");
        }
    }
}
