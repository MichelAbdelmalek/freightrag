using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FreightRag.Api.Endpoints;
using FreightRag.Tests.Support;

namespace FreightRag.Tests.Api;

public sealed class DocumentEndpointsTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    private static MultipartFormDataContent Upload(string fileName, byte[] content)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private static MultipartFormDataContent RealFile(string tenant, string fileName) =>
        Upload(fileName, TestData.ReadBytes(tenant, fileName));

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    // ---------- authentication ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_document_endpoint_requires_a_valid_api_key(bool sendWrongKey)
    {
        var client = _factory.ClientWithKey(sendWrongKey ? TestSecrets.NewFakeSecret() : null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/documents", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/documents/{Guid.NewGuid()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt"), Ct)).StatusCode);
        Assert.Empty(_factory.Repository.Stored);
    }

    // ---------- POST /api/v1/documents ----------

    [Fact]
    public async Task Uploading_a_new_document_returns_201_with_its_location()
    {
        var client = _factory.ClientWithKey(ApiFactory.NileKey);

        var response = await client.PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "rate-confirmation-48213.txt"), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        var stored = Assert.Single(_factory.Repository.Stored);
        Assert.Equal(stored.Id, body.GetProperty("id").GetGuid());
        Assert.Equal("rate-confirmation-48213", body.GetProperty("title").GetString());
        Assert.Equal("rate-confirmation", body.GetProperty("docType").GetString());
        Assert.Equal(stored.Chunks.Count, body.GetProperty("chunkCount").GetInt32());
        Assert.Equal($"/api/v1/documents/{stored.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("tenant-nile-freight", TenantSlugOf(stored.TenantId));
    }

    [Fact]
    public async Task Uploading_the_same_bytes_again_returns_200_skipped()
    {
        var client = _factory.ClientWithKey(ApiFactory.NileKey);
        await client.PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt"), Ct);

        var response = await client.PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("skipped", (await Json(response)).GetProperty("status").GetString());
        Assert.Single(_factory.Repository.Stored);
    }

    [Fact]
    public async Task The_tenant_comes_from_the_key_not_from_the_request()
    {
        await _factory.ClientWithKey(ApiFactory.DeltaKey)
            .PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt"), Ct);

        Assert.Equal("tenant-delta-haul", TenantSlugOf(Assert.Single(_factory.Repository.Stored).TenantId));
    }

    [Fact]
    public async Task An_unknown_document_type_is_422_problem_details()
    {
        var response = await _factory.ClientWithKey(ApiFactory.NileKey)
            .PostAsync("/api/v1/documents", Upload("packing-list-1.txt", "PACKING LIST\n3 pallets"u8.ToArray()), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("packing-list-1.txt", (await Json(response)).GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("../tenant-delta-haul/invoice-X-1.txt")]
    [InlineData("folder/invoice-X-1.txt")]
    [InlineData("..\\invoice-X-1.txt")]
    [InlineData("invoice-X-1.pdf")]
    [InlineData("invoice-a\u202Eb.txt")]   // Unicode format character (right-to-left override)
    public async Task Path_like_or_non_txt_file_names_are_400(string fileName)
    {
        var response = await _factory.ClientWithKey(ApiFactory.NileKey)
            .PostAsync("/api/v1/documents", Upload(fileName, "INVOICE\nTotal: $1"u8.ToArray()), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Repository.Stored);
    }

    [Theory]
    [InlineData("invoice-a%00b.txt")]   // NUL
    [InlineData("invoice-a%0Ab.txt")]   // newline
    public async Task File_names_with_control_characters_are_400(string percentEncodedName)
    {
        // HttpClient refuses such names in a plain filename="…", but RFC 5987 filename*=UTF-8''… carries
        // them, and ASP.NET Core decodes it — so that is what an attacker would send.
        var file = new ByteArrayContent("INVOICE\nTotal: $1"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename*=UTF-8''{percentEncodedName}");
        var form = new MultipartFormDataContent { file };

        var response = await _factory.ClientWithKey(ApiFactory.NileKey).PostAsync("/api/v1/documents", form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Embeddings.Calls);
    }

    [Fact]
    public async Task A_missing_file_is_400()
    {
        var response = await _factory.ClientWithKey(ApiFactory.NileKey)
            .PostAsync("/api/v1/documents", new MultipartFormDataContent { { new StringContent("x"), "notfile" } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_file_over_10_MB_is_413()
    {
        var tooBig = new byte[DocumentEndpoints.MaxUploadBytes + 1];
        Array.Fill(tooBig, (byte)'x');

        var response = await _factory.ClientWithKey(ApiFactory.NileKey)
            .PostAsync("/api/v1/documents", Upload("invoice-BIG-1.txt", tooBig), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_factory.Embeddings.Calls);
    }

    [Fact]
    public async Task Uploads_are_rate_limited_per_tenant()
    {
        using var factory = new ApiFactory { IngestPermitsPerMinute = 2 };
        var nile = factory.ClientWithKey(ApiFactory.NileKey);

        Assert.Equal(HttpStatusCode.Created, (await nile.PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await nile.PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "rate-confirmation-48213.txt"), Ct)).StatusCode);
        var limited = await nile.PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "bill-of-lading-SAV-778431.txt"), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero, "429 must tell clients when to retry (Retry-After).");

        // Another tenant has its own budget.
        var delta = factory.ClientWithKey(ApiFactory.DeltaKey);
        Assert.Equal(HttpStatusCode.Created, (await delta.PostAsync("/api/v1/documents", RealFile(TestData.DeltaHaul, "invoice-DH-26-0412.txt"), Ct)).StatusCode);
    }

    // ---------- GET /api/v1/documents ----------

    [Fact]
    public async Task Listing_returns_only_the_callers_documents_with_paging()
    {
        var nile = _factory.ClientWithKey(ApiFactory.NileKey);
        await nile.PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt"), Ct);
        await nile.PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "rate-confirmation-48213.txt"), Ct);
        await _factory.ClientWithKey(ApiFactory.DeltaKey)
            .PostAsync("/api/v1/documents", RealFile(TestData.DeltaHaul, "invoice-DH-26-0412.txt"), Ct);

        var body = await Json(await nile.GetAsync("/api/v1/documents?page=1&pageSize=1", Ct));

        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, body.GetProperty("totalPages").GetInt32());
        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("rate-confirmation-48213", item.GetProperty("title").GetString()); // newest first
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?page=abc")]           // binding failure
    [InlineData("?page=99999999999")]   // overflows int
    public async Task Invalid_paging_is_400_problem_details(string query)
    {
        var response = await _factory.ClientWithKey(ApiFactory.NileKey).GetAsync($"/api/v1/documents{query}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Bad_input_is_400_even_in_the_Development_environment()
    {
        // In Development, minimal APIs throw on binding errors by default, which the exception handler
        // would turn into a 500. The API switches that off so every environment behaves the same.
        using var factory = new ApiFactory { Environment = "Development" };

        var response = await factory.ClientWithKey(ApiFactory.NileKey).GetAsync("/api/v1/documents?page=abc", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Posting_json_instead_of_a_file_is_415()
    {
        var response = await _factory.ClientWithKey(ApiFactory.NileKey)
            .PostAsync("/api/v1/documents", JsonContent.Create(new { file = "x" }), Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // ---------- GET /api/v1/documents/{id} ----------

    [Fact]
    public async Task Getting_a_document_returns_its_chunks_in_order()
    {
        var nile = _factory.ClientWithKey(ApiFactory.NileKey);
        var created = await Json(await nile.PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "rate-confirmation-48213.txt"), Ct));
        var id = created.GetProperty("id").GetGuid();

        var body = await Json(await nile.GetAsync($"/api/v1/documents/{id}", Ct));

        var chunks = body.GetProperty("chunks").EnumerateArray().ToList();
        Assert.Equal(created.GetProperty("chunkCount").GetInt32(), chunks.Count);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.GetProperty("index").GetInt32()));
        Assert.StartsWith("Document: rate-confirmation | Load: 48213 | Section: ", chunks[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Another_tenants_document_is_404_exactly_like_a_missing_one()
    {
        var created = await Json(await _factory.ClientWithKey(ApiFactory.NileKey)
            .PostAsync("/api/v1/documents", RealFile(TestData.NileFreight, "invoice-NFL-2026-1187.txt"), Ct));
        var delta = _factory.ClientWithKey(ApiFactory.DeltaKey);

        var otherTenants = await delta.GetAsync($"/api/v1/documents/{created.GetProperty("id").GetGuid()}", Ct);
        var missing = await delta.GetAsync($"/api/v1/documents/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, otherTenants.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        // Same shape of answer, so a caller can't probe whether an id exists in another tenant.
        Assert.Equal(missing.Content.Headers.ContentType?.MediaType, otherTenants.Content.Headers.ContentType?.MediaType);
    }

    private static string TenantSlugOf(FreightRag.Core.Domain.TenantId id) =>
        id.Value == Guid.Parse("de1c81e9-4aa5-4f07-87bd-c5d6f48ff097") ? "tenant-nile-freight"
        : id.Value == Guid.Parse("14a2de5d-56c5-4eda-a737-d957f4580b90") ? "tenant-delta-haul"
        : "unknown";
}
