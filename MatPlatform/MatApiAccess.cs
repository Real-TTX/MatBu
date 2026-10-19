// MatPlatform 1.3.0 – a copy from MatDesign, do not edit here
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MatPlatform;

/// <summary>
/// Who may call the protected part of <c>/api</c> (MatDesign docs/PLATFORM.md, "Access"): a caller with the key <c>MAT_API_KEY</c> in the
/// header <c>X-Api-Key</c> (compared in constant time; no key configured = the key is off), or, when MatIdentity is configured, a caller
/// with an access token of MatIdentity for the audience of this app (the client id), checked by the scheme <see cref="BearerScheme"/>
/// that <c>AddMatIdentitySignIn</c> registers.
/// </summary>
public sealed class MatApiAccess
{
    public const string HeaderName = "X-Api-Key";

    /// <summary>Where the name of the caller is kept in <see cref="HttpContext.Items"/>.</summary>
    public const string CallerKey = "matplatform:api-caller";

    /// <summary>The JWT bearer scheme that validates tokens of MatIdentity (registered only when MatIdentity is configured).</summary>
    public const string BearerScheme = "MatIdentityBearer";

    private readonly byte[]? _key;
    private readonly bool _bearer;

    public MatApiAccess(MatPlatformSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _key = settings.ApiKey is null ? null : Encoding.UTF8.GetBytes(settings.ApiKey);
        _bearer = settings.IdentityEnabled;
    }

    public bool KeyEnabled => _key is not null;

    /// <summary>The name of the caller for the activity log (<c>api-key</c>, the subject or the client of a token), or null when it may not call.</summary>
    public async Task<string?> AuthenticateAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (_key is not null && http.Request.Headers.TryGetValue(HeaderName, out var sent) && sent.Count == 1)
        {
            var given = Encoding.UTF8.GetBytes(sent.ToString());
            if (CryptographicOperations.FixedTimeEquals(given, _key))
            {
                return "api-key";
            }
        }

        if (_bearer
            && http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && await http.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(BearerScheme) is not null)
        {
            // Issuer, signature (JWKS of MatIdentity), lifetime and audience are checked by the scheme.
            var result = await http.AuthenticateAsync(BearerScheme);
            if (result.Succeeded)
            {
                return result.Principal.FindFirst("sub")?.Value
                    ?? result.Principal.FindFirst("client_id")?.Value
                    ?? "token";
            }
        }

        return null;
    }

    /// <summary>An endpoint filter: <c>401</c> unless <see cref="AuthenticateAsync"/> knows the caller; the name lands in <see cref="HttpContext.Items"/> under <see cref="CallerKey"/>.</summary>
    public static async ValueTask<object?> RequireAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var caller = await http.RequestServices.GetRequiredService<MatApiAccess>().AuthenticateAsync(http);
        if (caller is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Json(
                new { errors = new Dictionary<string, string> { [string.Empty] = "A valid X-Api-Key or bearer token is required." } },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        http.Items[CallerKey] = caller;
        return await next(context);
    }

    /// <summary>The caller that <see cref="RequireAsync"/> let in, or <paramref name="fallback"/>.</summary>
    public static string CallerOf(HttpContext http, string fallback = "api") => http?.Items[CallerKey] as string ?? fallback;
}
