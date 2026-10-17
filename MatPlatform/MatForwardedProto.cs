// MatPlatform 1.2.0 – a copy from MatDesign, do not edit here
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace MatPlatform;

/// <summary>
/// The scheme of a request behind a TLS-terminating proxy (Matcad): the proxy speaks http to the app and says <c>X-Forwarded-Proto: https</c>.
/// Without it the sign-in through MatIdentity sees <c>http</c>: the <c>redirect_uri</c> is <c>http://.../signin-oidc</c> and the correlation and nonce
/// cookies are not <c>Secure</c>, so a browser drops them (they are <c>SameSite=None</c>) and the sign-in fails.
/// Only the scheme is taken from the header, from any sender: <c>RemoteIpAddress</c> (<c>X-Forwarded-For</c>, which guards the rate limits)
/// and <c>Host</c> stay the app's own policy. <c>X-Forwarded-Prefix</c> is not read: Matcad serves every app at the root of its own address.
/// On when MatIdentity is (<see cref="MatIdentitySignIn.AddMatIdentitySignIn{TUsers}"/>) and <see cref="MatIdentitySignInOptions.TrustForwardedProto"/> is set.
/// </summary>
public static class MatForwardedProto
{
    public const string HeaderName = "X-Forwarded-Proto";

    /// <summary>
    /// Sets the scheme of the request to <c>https</c> when the first value of <c>X-Forwarded-Proto</c> is <c>https</c> (any case); anything else changes
    /// nothing. Idempotent: the app's own ForwardedHeaders middleware, before or after it, comes to the same scheme.
    /// </summary>
    public static void Apply(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (IsHttps(http.Request.Headers[HeaderName].ToString()))
        {
            // Request.IsHttps follows the scheme.
            http.Request.Scheme = Uri.UriSchemeHttps;
        }
    }

    /// <summary>Whether the first value of the header (a comma list) is <c>https</c>.</summary>
    public static bool IsHttps(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        var comma = header.IndexOf(',', StringComparison.Ordinal);
        var first = (comma < 0 ? header : header[..comma]).Trim();
        return string.Equals(first, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Puts <see cref="MatForwardedProto.Apply"/> at the very start of the app's pipeline, before anything that reads the scheme.</summary>
internal sealed class MatForwardedProtoStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                MatForwardedProto.Apply(context);
                return nextMiddleware(context);
            });
            next(app);
        };
}
