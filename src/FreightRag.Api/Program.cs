// FreightRag.Api — HTTP adapter over the same use case and domain as the console ingest.
//
//   docker compose up api        → http://localhost:8080/scalar/v1   (interactive docs)
//                                  http://localhost:8080/openapi/v1.json
//
// Exit codes: 0 = stopped normally, 2 = invalid configuration at startup.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FreightRag.Api;
using FreightRag.Api.Configuration;
using FreightRag.Api.Endpoints;
using FreightRag.Api.Health;
using FreightRag.Api.OpenApi;
using FreightRag.Api.Security;
using FreightRag.Core.Application;
using FreightRag.Core.Application.Ports;
using FreightRag.Core.Application.Search;
using FreightRag.Core.Domain;
using FreightRag.Core.Infrastructure.Configuration;
using FreightRag.Core.Infrastructure.Embedding;
using FreightRag.Core.Infrastructure.Persistence;
using FreightRag.Core.Infrastructure.Tenants;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration.Json;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Secrets: only from User Secrets, kept out of the ordinary configuration ----
// In the Development environment ASP.NET Core adds User Secrets to builder.Configuration; remove that
// source so secrets can't be read (or logged) through IConfiguration. They live in SecretConfiguration.
foreach (var source in builder.Configuration.Sources.OfType<JsonConfigurationSource>().Where(s => s.Path == "secrets.json").ToList())
    builder.Configuration.Sources.Remove(source);
builder.Services.AddSingleton(new SecretConfiguration(
    new ConfigurationBuilder().AddUserSecrets(typeof(ApiAssemblyMarker).Assembly, optional: true).Build()));

// ---- Settings and infrastructure (built lazily; validated explicitly right after Build) ----
builder.Services.AddSingleton(sp => FreightRagSettings.Load(
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<SecretConfiguration>().Values, applicationName: "FreightRag.Api"));
builder.Services.AddSingleton<ITenantDirectory>(sp => new ConfiguredTenantDirectory(sp.GetRequiredService<FreightRagSettings>().Tenants));
builder.Services.AddSingleton(sp => FreightRagDbContextOptions.CreateDataSource(sp.GetRequiredService<FreightRagSettings>().Database.ConnectionString));
builder.Services.AddDbContextFactory<FreightRagDbContext>((sp, options) =>
    FreightRagDbContextOptions.Configure(options, sp.GetRequiredService<Npgsql.NpgsqlDataSource>()));
builder.Services.AddSingleton<IDocumentRepository, EfDocumentRepository>();
builder.Services.AddSingleton<IDocumentQueries, EfDocumentQueries>();
builder.Services.AddSingleton(sp => SearchSettings.Load(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => sp.GetRequiredService<SearchSettings>().ChunkSearch);
builder.Services.AddSingleton(sp => sp.GetRequiredService<SearchSettings>().Fusion);
builder.Services.AddSingleton(sp => sp.GetRequiredService<SearchSettings>().Candidates);
builder.Services.AddSingleton<IChunkSearch, EfChunkSearch>();
builder.Services.AddSingleton(sp =>
{
    var ollama = sp.GetRequiredService<FreightRagSettings>().Ollama;
    return OllamaClientFactory.CreateEmbeddingGenerator(ollama.Url, ollama.EmbeddingModel);
});
builder.Services.AddSingleton<IEmbeddingService>(sp => new OllamaEmbeddingService(
    sp.GetRequiredService<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(),
    new EmbeddingModelName(sp.GetRequiredService<FreightRagSettings>().Ollama.EmbeddingModel)));
builder.Services.AddSingleton<SectionChunker>();
builder.Services.AddSingleton<IngestDocumentHandler>();
builder.Services.AddSingleton<SearchChunksHandler>();
builder.Services.AddSingleton(sp => new ApiKeyStore(
    sp.GetRequiredService<SecretConfiguration>().Values, sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ITenantDirectory>()));

// ---- Authentication: API key → tenant ----
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddSingleton(sp => RateLimitSettings.Load(sp.GetRequiredService<IConfiguration>()));

// Binding errors (e.g. page=abc) are 400 in every environment. (In Development minimal APIs would
// otherwise throw, and the exception handler would turn that into a 500.)
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

// ---- Abuse protection ----
// Uploads call the embedding model (expensive), so each tenant gets its own fixed window.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Tell clients when to retry instead of letting them hammer the API until the window resets.
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return ValueTask.CompletedTask;
    };
    options.AddPolicy(DocumentEndpoints.IngestRateLimitPolicy, context =>
    {
        var permits = context.RequestServices.GetRequiredService<RateLimitSettings>().IngestPermitsPerMinute;
        return PerTenantFixedWindow(context, permits);
    });
    // Each search also calls the embedding model, so searches get their own per-tenant budget.
    options.AddPolicy(SearchEndpoints.SearchRateLimitPolicy, context =>
    {
        var permits = context.RequestServices.GetRequiredService<RateLimitSettings>().SearchPermitsPerMinute;
        return PerTenantFixedWindow(context, permits);
    });

    static RateLimitPartition<string> PerTenantFixedWindow(HttpContext context, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(TenantClaims.TenantSlug)?.Value ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
});
// Bound request bodies a little above the file limit (multipart overhead); the endpoint returns 413 for big files.
const long maxRequestBytes = DocumentEndpoints.MaxUploadBytes + 64 * 1024;
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = maxRequestBytes);
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = maxRequestBytes);

// ---- JSON: numbers are numbers (the web default also accepts "42" as a string, which would make every
// integer show up as integer-or-string in the OpenAPI document) ----
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    // Enums as lower-case strings ("hybrid"), never as numbers; reading is case-insensitive.
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
});

// ---- Errors as RFC 9457 problem details (no stack traces outside Development) ----
builder.Services.AddProblemDetails();

// ---- Health ----
builder.Services.AddHttpClient();
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<OllamaHealthCheck>("ollama", tags: ["ready"]);

// ---- OpenAPI document (built-in generator) ----
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<ApiKeyDocumentTransformer>();
    options.AddOperationTransformer<ApiKeyOperationTransformer>();
});

var app = builder.Build();

// Fail fast on misconfiguration (missing password, bad tenant GUID, short API key, ...): exit code 2
// with an actionable message instead of failing on the first request.
// (Integration tests stop the program at Build() and supply valid test settings themselves.)
try
{
    _ = app.Services.GetRequiredService<FreightRagSettings>();
    _ = app.Services.GetRequiredService<ApiKeyStore>();
    _ = app.Services.GetRequiredService<RateLimitSettings>();
    _ = app.Services.GetRequiredService<SearchSettings>();
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DomainException)
{
    app.Logger.LogCritical("{Message}", ex.Message); // ArgumentOutOfRangeException (e.g. Search:MaxScanTuples) is an ArgumentException
    return 2;
}

app.UseExceptionHandler();
app.UseStatusCodePages(); // gives 401/404/429 a problem-details body too
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // after auth, so limits are per tenant

app.MapDocumentEndpoints();
app.MapSearchEndpoints();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }); // process is up
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

// Interactive docs: on by default for this learning project; set OpenApi:Enabled=false in production.
if (app.Configuration.GetValue("OpenApi:Enabled", true))
{
    app.MapOpenApi();                                   // /openapi/v1.json
    app.MapScalarApiReference(options => options       // /scalar/v1
        .WithTitle("FreightRAG API")
        .AddPreferredSecuritySchemes(ApiKeyAuthenticationHandler.SchemeName)
        // Learning project: the Nile Freight key is prefilled so "Send" works straight away. It is a test key for
        // synthetic data, committed on purpose; the Delta Haul key is in docs/articles/getting-started.md.
        .AddApiKeyAuthentication(ApiKeyAuthenticationHandler.SchemeName, apiKey => apiKey.Value = "5MFVYAQlQzZn0FmFT7GmKIymfFewhbk1n8l/RrTRx44="));
}

await app.RunAsync();
return 0;
