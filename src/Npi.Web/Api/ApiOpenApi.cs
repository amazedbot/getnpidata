using Microsoft.OpenApi;
using Npi.Web.Search;

namespace Npi.Web.Api;

/// <summary>The OpenAPI document (/openapi/v1.json) behind the Swagger UI at /swagger.</summary>
public static class ApiOpenApi
{
    public const string DocumentUrl = "/openapi/v1.json";

    public static IServiceCollection AddApiOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(o =>
        {
            o.ShouldInclude = d => d.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true;
            o.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "getnpidata API",
                    Version = "v1",
                    Description =
                        "Search every active US healthcare provider in the CMS NPPES registry. Public data; deactivated NPIs are never returned. " +
                        "Requests are rate-limited per client IP (HTTP 429 with Retry-After). Errors are RFC 7807 problem details. " +
                        $"If the operator turns on API keys, send yours in the {ApiKeyFilter.HeaderName} header.",
                };
                return Task.CompletedTask;
            });
        });

    /// <summary>
    /// Documents the search query parameters. The endpoints read them from the query string with
    /// <see cref="SearchQueryString.Parse"/> (the same parser as the search page), so they are listed here
    /// from <see cref="SearchQueryString.Parameters"/> instead of being bound as handler arguments.
    /// </summary>
    public static RouteHandlerBuilder WithSearchParameters(this RouteHandlerBuilder builder) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            foreach (var p in SearchQueryString.Parameters)
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = p.Name,
                    In = ParameterLocation.Query,
                    Description = p.Description,
                    Schema = new OpenApiSchema
                    {
                        Type = p.Type switch
                        {
                            SearchQueryString.ParameterType.WholeNumber => JsonSchemaType.Integer,
                            SearchQueryString.ParameterType.TrueFalse => JsonSchemaType.Boolean,
                            _ => JsonSchemaType.String,
                        },
                    },
                });
            }

            return Task.CompletedTask;
        });
}
