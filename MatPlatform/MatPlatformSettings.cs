// MatPlatform 1.4.0 – a copy from MatDesign, do not edit here
using Microsoft.Extensions.Configuration;

namespace MatPlatform;

/// <summary>
/// The environment of the Mat platform (MatDesign docs/PLATFORM.md): the key of the protected part of <c>/api</c> and the optional
/// sign-in through MatIdentity. Read once at startup from the configuration (environment variables have the same names).
/// </summary>
public sealed class MatPlatformSettings
{
    public const string ApiKeyVariable = "MAT_API_KEY";
    public const string AuthorityVariable = "MATIDENTITY_AUTHORITY";
    public const string ClientIdVariable = "MATIDENTITY_CLIENT_ID";
    public const string ClientSecretVariable = "MATIDENTITY_CLIENT_SECRET";
    public const string InternalUrlVariable = "MATIDENTITY_INTERNAL_URL";

    /// <summary>The id of the app in the platform (and the default client id in MatIdentity).</summary>
    public required string AppId { get; init; }

    /// <summary>The key for <c>X-Api-Key</c>; null = the key is off.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The public address of MatIdentity, without a trailing slash; null = sign-in through MatIdentity is off.</summary>
    public string? Authority { get; init; }

    /// <summary>The client of the app in MatIdentity, also the audience of the tokens the app accepts on <c>/api</c>; default: <see cref="AppId"/>.</summary>
    public string ClientId { get; init; } = string.Empty;

    public string? ClientSecret { get; init; }

    /// <summary>The address MatIdentity is reached at from this app (inside Docker); null = the public address.</summary>
    public string? InternalUrl { get; init; }

    public bool IdentityEnabled => Authority is not null;

    public static MatPlatformSettings From(IConfiguration configuration, string appId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        return new MatPlatformSettings
        {
            AppId = appId,
            ApiKey = Clean(configuration[ApiKeyVariable]),
            Authority = Clean(configuration[AuthorityVariable])?.TrimEnd('/'),
            ClientId = Clean(configuration[ClientIdVariable]) ?? appId,
            ClientSecret = Clean(configuration[ClientSecretVariable]),
            InternalUrl = Clean(configuration[InternalUrlVariable])?.TrimEnd('/'),
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
