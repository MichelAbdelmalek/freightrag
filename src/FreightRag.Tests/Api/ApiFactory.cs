using FreightRag.Api;
using FreightRag.Api.Configuration;
using FreightRag.Core.Application.Ports;
using FreightRag.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FreightRag.Tests.Api;

/// <summary>
/// Runs the real API in memory (routing, auth, rate limiting, OpenAPI) but with in-memory fakes for
/// Postgres and Ollama, and test-only secrets instead of User Secrets. No network, no database.
/// </summary>
internal sealed class ApiFactory : WebApplicationFactory<ApiAssemblyMarker>
{
    // Generated per test run: no credential literals in the source.
    public static readonly string NileKey = TestSecrets.NewFakeSecret();
    public static readonly string DeltaKey = TestSecrets.NewFakeSecret();

    public InMemoryDocumentRepository Repository { get; } = new();
    public FakeEmbeddingService Embeddings { get; } = new();
    public int IngestPermitsPerMinute { get; init; } = 1000;
    public string Environment { get; init; } = "Testing";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("RateLimiting:IngestPermitsPerMinute", IngestPermitsPerMinute.ToString());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<SecretConfiguration>();
            services.AddSingleton(new SecretConfiguration(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Password"] = TestSecrets.NewFakeSecret(),
                    ["ApiKeys:tenant-nile-freight"] = NileKey,
                    ["ApiKeys:tenant-delta-haul"] = DeltaKey,
                })
                .Build()));

            services.RemoveAll<IDocumentRepository>();
            services.AddSingleton<IDocumentRepository>(Repository);
            services.RemoveAll<IEmbeddingService>();
            services.AddSingleton<IEmbeddingService>(Embeddings);
            services.RemoveAll<IDocumentQueries>();
            services.AddSingleton<IDocumentQueries>(new InMemoryDocumentQueries(Repository));
        });
    }

    public HttpClient ClientWithKey(string? apiKey)
    {
        var client = CreateClient();
        if (apiKey is not null)
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }
}
