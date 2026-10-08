using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Npi.Web.Api;

/// <summary>Rejects /api/v1 requests without a valid <c>X-Api-Key</c> when <see cref="ApiOptions.RequireKey"/> is on.</summary>
public sealed class ApiKeyFilter(IOptions<ApiOptions> options) : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly byte[][] _keys = [.. options.Value.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => Encoding.UTF8.GetBytes(k))];

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!options.Value.RequireKey || IsValid(context.HttpContext.Request.Headers[HeaderName].ToString()))
        {
            return next(context);
        }

        return ValueTask.FromResult<object?>(Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "API key required",
            detail: $"Send a valid key in the {HeaderName} header."));
    }

    private bool IsValid(string presented)
    {
        if (presented.Length == 0)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(presented);
        var ok = false;
        foreach (var key in _keys)
        {
            ok |= CryptographicOperations.FixedTimeEquals(bytes, key); // check every key, so timing doesn't reveal which matched
        }

        return ok;
    }
}
