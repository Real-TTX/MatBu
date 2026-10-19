// MatPlatform 1.3.0 – a copy from MatDesign, do not edit here
namespace MatPlatform;

/// <summary>
/// The server-to-server calls to MatIdentity (discovery document, JWKS, token endpoint, user info) when the app reaches it at
/// another address than the browser does (inside Docker, <c>MATIDENTITY_INTERNAL_URL</c>): every request whose address starts with
/// the public authority goes to the internal base instead (scheme, host and port replaced, the rest of the path kept). The browser
/// redirects and the expected issuer stay public; MatIdentity runs with a fixed issuer, so tokens fetched internally carry it too.
/// </summary>
public sealed class MatIdentityBackchannelHandler : DelegatingHandler
{
    private readonly Uri _public;
    private readonly Uri _internal;

    public MatIdentityBackchannelHandler(Uri publicAuthority, Uri internalBase)
        : this(publicAuthority, internalBase, new HttpClientHandler())
    {
    }

    public MatIdentityBackchannelHandler(Uri publicAuthority, Uri internalBase, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(publicAuthority);
        ArgumentNullException.ThrowIfNull(internalBase);
        _public = publicAuthority;
        _internal = internalBase;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri && Rewrite(uri) is { } rewritten)
        {
            request.RequestUri = rewritten;
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>The internal address of <paramref name="uri"/>, or null when it is not an address of the public authority.</summary>
    public Uri? Rewrite(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, _public.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, _public.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _public.Port)
        {
            return null;
        }

        var publicPath = _public.AbsolutePath.TrimEnd('/');
        var path = uri.AbsolutePath;
        if (publicPath.Length > 0
            && !(path.Equals(publicPath, StringComparison.OrdinalIgnoreCase)
                 || path.StartsWith(publicPath + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = _internal.Scheme,
            Host = _internal.Host,
            Port = _internal.Port,
            Path = _internal.AbsolutePath.TrimEnd('/') + path[publicPath.Length..],
        };
        return builder.Uri;
    }
}
