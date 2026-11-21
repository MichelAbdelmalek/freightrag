using System.Net;
using System.Text.Json.Nodes;

namespace FreightRag.Tests.Api;

public sealed class OpenApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    private async Task<string> OpenApiJson()
    {
        var response = await _factory.CreateClient().GetAsync("/openapi/v1.json", Ct); // anonymous on purpose
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    [Fact]
    public async Task Describes_every_document_endpoint()
    {
        var doc = JsonNode.Parse(await OpenApiJson())!;
        var paths = doc["paths"]!.AsObject();

        Assert.NotNull(paths["/api/v1/documents"]?["post"]);
        Assert.NotNull(paths["/api/v1/documents"]?["get"]);
        Assert.NotNull(paths["/api/v1/documents/{id}"]?["get"]);
        Assert.Equal("FreightRAG API", doc["info"]?["title"]?.GetValue<string>());
    }

    [Fact]
    public async Task Declares_the_api_key_scheme_and_requires_it_on_document_endpoints()
    {
        var doc = JsonNode.Parse(await OpenApiJson())!;

        var scheme = doc["components"]?["securitySchemes"]?["ApiKey"];
        Assert.Equal("apiKey", scheme?["type"]?.GetValue<string>());
        Assert.Equal("header", scheme?["in"]?.GetValue<string>());
        Assert.Equal("X-Api-Key", scheme?["name"]?.GetValue<string>());

        var post = doc["paths"]!["/api/v1/documents"]!["post"]!;
        Assert.NotNull(post["security"]?[0]?["ApiKey"]);
        // The 401 has a problem-details body (UseStatusCodePages), so the document must say so.
        Assert.NotNull(post["responses"]?["401"]?["content"]?["application/problem+json"]?["schema"]);
    }

    [Fact]
    public async Task Documents_the_upload_status_codes()
    {
        var responses = JsonNode.Parse(await OpenApiJson())!["paths"]!["/api/v1/documents"]!["post"]!["responses"]!.AsObject();

        foreach (var code in new[] { "200", "201", "400", "401", "413", "415", "422", "429" })
            Assert.True(responses.ContainsKey(code), $"POST /api/v1/documents should document {code}");
    }

    [Fact]
    public async Task Matches_the_committed_snapshot_in_docs()
    {
        // The committed docs/openapi/v1.json feeds the documentation site. If the API changes, this test
        // fails until the snapshot is regenerated, so API changes are always visible in the PR diff.
        // Regenerate with the bind-mounted command in the README ("If you change the API"); the compose
        // `tests` service would only update its own copy inside the container.
        var actual = JsonNode.Parse(await OpenApiJson())!;
        var path = Path.Combine(RepoRoot(), "docs", "openapi", "v1.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual.ToJsonString(new() { WriteIndented = true }) + "\n", Ct);
        }

        Assert.True(File.Exists(path), $"Missing {path}. Run the tests once with UPDATE_OPENAPI_SNAPSHOT=1.");
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct));
        Assert.True(JsonNode.DeepEquals(expected, actual),
            "The API changed but docs/openapi/v1.json was not updated. Re-run the tests with UPDATE_OPENAPI_SNAPSHOT=1 and commit the file.");
    }

    [Fact]
    public async Task List_documents_400_is_plain_problem_details()
    {
        // Validation and binding errors on paging return the same shape, and the document says which.
        var get = JsonNode.Parse(await OpenApiJson())!["paths"]!["/api/v1/documents"]!["get"]!;
        var schemaRef = get["responses"]?["400"]?["content"]?["application/problem+json"]?["schema"]?["$ref"]?.GetValue<string>();

        Assert.Equal("#/components/schemas/ProblemDetails", schemaRef);
    }

    [Fact]
    public async Task Describes_search_and_warns_that_content_is_untrusted()
    {
        var doc = JsonNode.Parse(await OpenApiJson())!;
        var post = doc["paths"]?["/api/v1/search"]?["post"];

        Assert.NotNull(post);
        Assert.NotNull(post["security"]?[0]?["ApiKey"]);
        foreach (var code in new[] { "200", "400", "401", "429" })
            Assert.True(post["responses"]!.AsObject().ContainsKey(code), $"POST /api/v1/search should document {code}");

        var hit = doc["components"]?["schemas"]?["SearchHitResponse"]?["properties"];
        Assert.Contains("untrusted", hit?["content"]?["description"]?.GetValue<string>());
        foreach (var field in new[] { "vectorRank", "keywordRank", "fusedScore", "distance" })
            Assert.NotNull(hit?[field]);
    }

    [Fact]
    public async Task Documents_the_search_mode_as_an_enum_with_hybrid_as_default()
    {
        var schemas = JsonNode.Parse(await OpenApiJson())!["components"]!["schemas"]!;
        var mode = schemas["SearchRequest"]?["properties"]?["mode"]!;

        // An optional enum is written as oneOf/anyOf [null, {$ref: SearchModeOption}]; accept either shape.
        var alternatives = (mode["oneOf"] ?? mode["anyOf"])?.AsArray().Select(n => n!).ToList() ?? [mode];
        var reference = alternatives.First(n => n["$ref"] is not null);
        var enumSchema = schemas[reference["$ref"]!.GetValue<string>().Split('/')[^1]]!;

        var values = enumSchema["enum"]!.AsArray().Where(v => v is not null).Select(v => v!.GetValue<string>()).ToList();
        Assert.Equal(["vector", "keyword", "hybrid"], values);
        Assert.Equal("hybrid", (reference["default"] ?? mode["default"])?.GetValue<string>());
    }

    [Fact]
    public async Task Serves_the_scalar_ui()
    {
        var response = await _factory.CreateClient().GetAsync("/scalar/v1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Liveness_is_anonymous_and_healthy()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "FreightRag.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Could not find the repository root (src/FreightRag.slnx).");
    }
}
