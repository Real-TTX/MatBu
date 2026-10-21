// MatPlatform 1.4.0 – a copy from MatDesign, do not edit here
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MatPlatform;

/// <summary>
/// A logout token of MatIdentity (OpenID Connect Back-Channel Logout 1.0, MatDesign docs/PLATFORM.md "Back-channel logout") once it has been checked:
/// whose sessions end. <see cref="Subject"/> alone means every session of the person (MatIdentity signed them out everywhere).
/// </summary>
/// <param name="Subject">The claim <c>sub</c>, or null.</param>
/// <param name="SessionId">The claim <c>sid</c> (the session at MatIdentity, <see cref="MatIdentityPerson.SessionId"/>), or null.</param>
public sealed record MatIdentityLogoutToken(string? Subject, string? SessionId)
{
    /// <summary>The member of the <c>events</c> claim that makes a JWT a logout token.</summary>
    public const string EventType = "http://schemas.openid.net/event/backchannel-logout";

    /// <summary>The <c>typ</c> of the header MatIdentity writes; a token with another <c>typ</c> (an ID token, an access token) is refused.</summary>
    public const string TokenType = "logout+jwt";

    /// <summary>How far the clocks of MatIdentity and the app may differ.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Checks <paramref name="logoutToken"/> as the specification says: the signature with the keys of the discovery document (the configuration of the
    /// OpenID Connect handler, so through <c>MATIDENTITY_INTERNAL_URL</c> too; the keys are fetched again once for an unknown key), <c>iss</c> = the issuer
    /// of the document, <c>aud</c> contains <paramref name="clientId"/>, <c>iat</c> not older than <paramref name="maxAge"/> and not in the future,
    /// <c>exp</c> when it is there, the <c>events</c> claim with <see cref="EventType"/>, <c>sub</c> or <c>sid</c>, no <c>nonce</c>, a <c>jti</c> that
    /// <paramref name="replay"/> has not seen (it is remembered only when everything else holds).
    /// </summary>
    public static async Task<MatIdentityLogoutTokenCheck> ValidateAsync(
        string? logoutToken,
        OpenIdConnectOptions oidc,
        string clientId,
        TimeSpan maxAge,
        MatIdentityLogoutReplayCache replay,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(oidc);
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentNullException.ThrowIfNull(replay);

        if (string.IsNullOrWhiteSpace(logoutToken))
        {
            return MatIdentityLogoutTokenCheck.Refused("The form field logout_token is missing.");
        }

        OpenIdConnectConfiguration? configuration;
        try
        {
            configuration = await ConfigurationAsync(oidc, refresh: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MatIdentityLogoutTokenCheck.Refused("The keys of MatIdentity could not be read: " + ex.Message);
        }

        if (configuration is null || string.IsNullOrEmpty(configuration.Issuer))
        {
            return MatIdentityLogoutTokenCheck.Refused("The discovery document of MatIdentity names no issuer.");
        }

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var result = await handler.ValidateTokenAsync(logoutToken, Parameters(configuration, clientId));
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException && oidc.ConfigurationManager is { } manager)
        {
            // MatIdentity may have a new key: read the document once more.
            manager.RequestRefresh();
            configuration = await ConfigurationAsync(oidc, refresh: true, ct);
            if (configuration is not null && !string.IsNullOrEmpty(configuration.Issuer))
            {
                result = await handler.ValidateTokenAsync(logoutToken, Parameters(configuration, clientId));
            }
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return MatIdentityLogoutTokenCheck.Refused("The token is not valid: " + (result.Exception?.Message ?? "unknown reason"));
        }

        if (!string.IsNullOrEmpty(jwt.Typ)
            && !string.Equals(jwt.Typ, TokenType, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(jwt.Typ, "application/" + TokenType, StringComparison.OrdinalIgnoreCase))
        {
            return MatIdentityLogoutTokenCheck.Refused($"The token is of the type {jwt.Typ}, not {TokenType}.");
        }

        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));
        var root = payload.RootElement;

        if (!root.TryGetProperty("iat", out var iatValue) || iatValue.ValueKind != JsonValueKind.Number || !iatValue.TryGetInt64(out var iat))
        {
            return MatIdentityLogoutTokenCheck.Refused("The token has no iat.");
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iat);
        if (issuedAt > now + ClockSkew)
        {
            return MatIdentityLogoutTokenCheck.Refused("The token was issued in the future (iat).");
        }

        if (issuedAt < now - maxAge - ClockSkew)
        {
            return MatIdentityLogoutTokenCheck.Refused("The token is too old (iat).");
        }

        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Object
            || !events.TryGetProperty(EventType, out var logoutEvent) || logoutEvent.ValueKind != JsonValueKind.Object)
        {
            return MatIdentityLogoutTokenCheck.Refused("The token has no events claim with " + EventType + ".");
        }

        if (root.TryGetProperty("nonce", out _))
        {
            return MatIdentityLogoutTokenCheck.Refused("A logout token must not have a nonce.");
        }

        var subject = Text(root, "sub");
        var sessionId = Text(root, "sid");
        if (subject is null && sessionId is null)
        {
            return MatIdentityLogoutTokenCheck.Refused("The token names neither sub nor sid.");
        }

        if (Text(root, "jti") is not { } jti)
        {
            return MatIdentityLogoutTokenCheck.Refused("The token has no jti.");
        }

        if (!replay.TryRemember(jwt.Issuer + " " + jti, now + maxAge + ClockSkew + ClockSkew, now))
        {
            return MatIdentityLogoutTokenCheck.Refused("The token was used before (jti).");
        }

        return new MatIdentityLogoutTokenCheck(new MatIdentityLogoutToken(subject, sessionId), null);
    }

    private static async Task<OpenIdConnectConfiguration?> ConfigurationAsync(OpenIdConnectOptions oidc, bool refresh, CancellationToken ct)
    {
        if (oidc.ConfigurationManager is { } manager)
        {
            return await manager.GetConfigurationAsync(ct);
        }

        return refresh ? null : oidc.Configuration;
    }

    private static TokenValidationParameters Parameters(OpenIdConnectConfiguration configuration, string clientId) => new()
    {
        ValidIssuer = configuration.Issuer,
        ValidateIssuer = true,
        ValidAudience = clientId,
        ValidateAudience = true,
        IssuerSigningKeys = configuration.SigningKeys,
        RequireSignedTokens = true,
        // exp is checked when MatIdentity writes it (it does: two minutes); iat is checked by the window above.
        ValidateLifetime = true,
        RequireExpirationTime = false,
        ClockSkew = ClockSkew,
        ValidateTokenReplay = false,
    };

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()
            : null;
}

/// <summary>The outcome of <see cref="MatIdentityLogoutToken.ValidateAsync"/>: the token, or why it was refused.</summary>
public sealed record MatIdentityLogoutTokenCheck(MatIdentityLogoutToken? Token, string? Error)
{
    public bool IsValid => Token is not null;

    public static MatIdentityLogoutTokenCheck Refused(string error) => new(null, error);
}

/// <summary>The <c>jti</c> of the logout tokens already used, in memory, each until the token could no longer pass the <c>iat</c> window.</summary>
public sealed class MatIdentityLogoutReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>Remembers <paramref name="id"/> until <paramref name="until"/>; false when it is already remembered (a replay).</summary>
    public bool TryRemember(string id, DateTimeOffset until, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        foreach (var (key, expiry) in _seen)
        {
            if (expiry <= now)
            {
                _seen.TryRemove(key, out _);
            }
        }

        return _seen.TryAdd(id, until);
    }

    /// <summary>How many ids are remembered.</summary>
    public int Count => _seen.Count;
}
