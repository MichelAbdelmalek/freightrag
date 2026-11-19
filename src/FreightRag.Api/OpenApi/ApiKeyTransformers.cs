using FreightRag.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FreightRag.Api.OpenApi;

/// <summary>
/// Document-level OpenAPI tweaks: title/description, the <c>ApiKey</c> security scheme (gives Scalar
/// an "Authorize" box) and no absolute server URLs (so the committed snapshot is stable).
/// </summary>
internal sealed class ApiKeyDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "FreightRAG API";
        document.Info.Version = "v1";
        document.Info.Description =
            "Multi-tenant ingestion of logistics documents for retrieval-augmented generation. " +
            "Authenticate with your tenant's key in the X-Api-Key header; the tenant is derived from the key.";

        document.Servers?.Clear(); // relative URLs: works on any host and keeps the snapshot deterministic

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeyAuthenticationHandler.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthenticationHandler.HeaderName,
            Description = "Per-tenant API key (stored in User Secrets as ApiKeys:<tenant-slug>).",
        };
        return Task.CompletedTask;
    }
}

/// <summary>Marks operations that require authorization with the ApiKey requirement and a 401 response.</summary>
internal sealed class ApiKeyOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (!metadata.OfType<IAuthorizeData>().Any() || metadata.OfType<IAllowAnonymous>().Any())
            return Task.CompletedTask;

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(ApiKeyAuthenticationHandler.SchemeName, context.Document)] = [],
        });
        operation.Responses ??= new OpenApiResponses();
        // UseStatusCodePages + AddProblemDetails give the 401 a problem-details body; document it.
        operation.Responses.TryAdd("401", new OpenApiResponse
        {
            Description = "Missing or invalid API key.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference("ProblemDetails", context.Document) },
            },
        });
        return Task.CompletedTask;
    }
}
