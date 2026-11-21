using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FreightRag.Tests.Support;
using Microsoft.Extensions.Logging;

namespace FreightRag.Tests.Api;

public sealed class SearchEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    private async Task Upload(string apiKey, string tenant, string file)
    {
        var content = new ByteArrayContent(TestData.ReadBytes(tenant, file));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var response = await _factory.ClientWithKey(apiKey)
            .PostAsync("/api/v1/documents", new MultipartFormDataContent { { content, "file", file } }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> Search(string? apiKey, string json) =>
        _factory.ClientWithKey(apiKey).PostAsync("/api/v1/search", Json(json), Ct);

    [Fact]
    public async Task Requires_an_api_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Search(null, """{"query":"rate"}""")).StatusCode);
    }

    [Fact]
    public async Task Returns_an_ordered_list_of_chunks_with_their_distance()
    {
        await Upload(ApiFactory.NileKey, TestData.NileFreight, "rate-confirmation-48213.txt");

        var response = await Search(ApiFactory.NileKey, """{"query":"detention rate for load 48213","topK":3}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var hits = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToList();
        Assert.Equal(3, hits.Count);
        var stored = Assert.Single(_factory.Repository.Stored);
        foreach (var hit in hits)
        {
            Assert.NotEqual(Guid.Empty, hit.GetProperty("chunkId").GetGuid());
            Assert.Equal(stored.Id, hit.GetProperty("documentId").GetGuid());
            Assert.Equal("rate-confirmation-48213", hit.GetProperty("title").GetString());
            Assert.Equal("rate-confirmation", hit.GetProperty("docType").GetString());
            Assert.StartsWith("Document: rate-confirmation | Load: 48213 | Section: ", hit.GetProperty("content").GetString());
            Assert.True(hit.GetProperty("chunkIndex").GetInt32() >= 0);
        }
        var distances = hits.Select(h => h.GetProperty("distance").GetDouble()).ToList();
        Assert.Equal(distances.Order(), distances); // closest first
    }

    [Fact]
    public async Task Hits_carry_their_rank_in_each_leg_and_the_fused_score()
    {
        await Upload(ApiFactory.NileKey, TestData.NileFreight, "rate-confirmation-48213.txt");

        var hits = (await (await Search(ApiFactory.NileKey, """{"query":"detention rate 48213","topK":5}"""))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToList();

        Assert.All(hits, h => Assert.True(h.GetProperty("fusedScore").GetDouble() > 0));
        Assert.All(hits, h => Assert.True(h.TryGetProperty("vectorRank", out _) && h.TryGetProperty("keywordRank", out _)));
        Assert.Contains(hits, h => h.GetProperty("keywordRank").ValueKind == JsonValueKind.Number); // the keyword leg ran
        var scores = hits.Select(h => h.GetProperty("fusedScore").GetDouble()).ToList();
        Assert.Equal(scores.OrderDescending(), scores);
    }

    [Theory]
    [InlineData(null, "hybrid")]
    [InlineData("\"vector\"", "vector")]
    [InlineData("\"keyword\"", "keyword")]
    [InlineData("\"Hybrid\"", "hybrid")]
    public async Task Mode_defaults_to_hybrid_and_is_passed_through(string? modeJson, string expected)
    {
        var body = modeJson is null ? """{"query":"rate"}""" : $$"""{"query":"rate","mode":{{modeJson}}}""";

        var response = await Search(ApiFactory.NileKey, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, _factory.Search.Calls.Last().Mode.Name);
    }

    [Fact]
    public async Task Keyword_mode_results_have_no_vector_rank()
    {
        await Upload(ApiFactory.NileKey, TestData.NileFreight, "rate-confirmation-48213.txt");

        var hits = (await (await Search(ApiFactory.NileKey, """{"query":"detention rate","mode":"keyword"}"""))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToList();

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(JsonValueKind.Null, h.GetProperty("vectorRank").ValueKind));
        Assert.All(hits, h => Assert.True(h.GetProperty("distance").GetDouble() >= 0));
    }

    [Theory]
    [InlineData("""{"query":"rate","mode":"bm25"}""")]
    [InlineData("""{"query":"rate","mode":1}""")]
    [InlineData("""{"query":"rate","mode":""}""")]
    public async Task An_unknown_mode_is_400(string body)
    {
        var response = await Search(ApiFactory.NileKey, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Embeddings.QueryCalls);
    }

    [Fact]
    public async Task Returns_5_results_by_default()
    {
        await Upload(ApiFactory.NileKey, TestData.NileFreight, "rate-confirmation-48213.txt");

        var hits = await (await Search(ApiFactory.NileKey, """{"query":"rate"}""")).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(5, hits.GetArrayLength());
    }

    [Fact]
    public async Task The_tenant_comes_only_from_the_api_key_even_if_the_body_names_another()
    {
        await Upload(ApiFactory.DeltaKey, TestData.DeltaHaul, "rate-confirmation-77104.txt");

        // A Nile caller tries to smuggle Delta's tenant in the body: it is ignored.
        var response = await Search(ApiFactory.NileKey,
            """{"query":"rate","topK":20,"tenant":"tenant-delta-haul","tenantId":"14a2de5d-56c5-4eda-a737-d957f4580b90"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetArrayLength());
    }

    [Theory]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":"   "}""")]
    [InlineData("""{"topK":5}""")]
    [InlineData("""{"query":"rate","topK":0}""")]
    [InlineData("""{"query":"rate","topK":21}""")]
    [InlineData("""{"query":"rate","topK":"five"}""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public async Task Invalid_requests_are_400_problem_details_and_never_reach_the_model(string body)
    {
        var response = await Search(ApiFactory.NileKey, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(_factory.Embeddings.QueryCalls);
    }

    [Fact]
    public async Task A_query_over_1000_characters_is_400()
    {
        var response = await Search(ApiFactory.NileKey, JsonSerializer.Serialize(new { query = new string('q', 1001) }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Bodies_over_16_KB_are_refused_before_reaching_the_model()
    {
        var huge = JsonSerializer.Serialize(new { query = "a" + new string(' ', 20_000) });

        var response = await Search(ApiFactory.NileKey, huge);

        // Real Kestrel enforces the endpoint's 16 KB limit → 413 (verified end-to-end). The in-memory TestServer has
        // no request-size feature, so here the second line of defence answers: SearchText refuses raw input over
        // 2,000 characters → 400. Either way the request never reaches the embedding model.
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest });
        Assert.Empty(_factory.Embeddings.QueryCalls);
    }

    [Fact]
    public async Task Searches_are_rate_limited_per_tenant_with_retry_after()
    {
        using var factory = new ApiFactory { SearchPermitsPerMinute = 1 };
        var nile = factory.ClientWithKey(ApiFactory.NileKey);

        Assert.Equal(HttpStatusCode.OK, (await nile.PostAsync("/api/v1/search", Json("""{"query":"rate"}"""), Ct)).StatusCode);
        var limited = await nile.PostAsync("/api/v1/search", Json("""{"query":"rate"}"""), Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.ClientWithKey(ApiFactory.DeltaKey).PostAsync("/api/v1/search", Json("""{"query":"rate"}"""), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_query_text_is_never_logged_but_the_search_metrics_are()
    {
        await Upload(ApiFactory.NileKey, TestData.NileFreight, "invoice-NFL-2026-1187.txt");
        const string secretQuery = "confidential-margin-for-crescent-logistics-XYZZY";

        var response = await Search(ApiFactory.NileKey, JsonSerializer.Serialize(new { query = secretQuery, topK = 2 }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.DoesNotContain("XYZZY", _factory.Logs.AllText()); // at ANY level, in messages, values or exceptions

        var line = Assert.Single(_factory.Logs.Entries, e => e.Values.ContainsKey("QueryLength"));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal(secretQuery.Length, line.Values["QueryLength"]);
        Assert.Equal(2, line.Values["TopK"]);
        Assert.Equal(2, line.Values["ResultCount"]);
        Assert.Equal("hybrid", line.Values["Mode"]);
        Assert.True(line.Values.ContainsKey("VectorCandidates") && line.Values.ContainsKey("KeywordCandidates"));
        Assert.Equal(Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097"), line.Values["TenantId"]);
        Assert.True(line.Values.ContainsKey("DurationMs"));
    }
}
