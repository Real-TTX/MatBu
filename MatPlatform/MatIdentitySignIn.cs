// MatPlatform 1.3.0 – a copy from MatDesign, do not edit here
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MatPlatform;

/// <summary>A person MatIdentity has signed in, as the app gets it to find or create its local user.</summary>
/// <param name="Subject">The claim <c>sub</c>: the stable id of the person; match the local user by it first.</param>
/// <param name="Email">The claim <c>email</c>: an existing local user with this address is linked only after its password (<see cref="MatIdentitySignInResult.LinkRequired"/>).</param>
/// <param name="Name">The claim <c>name</c>.</param>
/// <param name="PreferredUserName">The claim <c>preferred_username</c>.</param>
/// <param name="Role">The role claim (<see cref="MatIdentitySignInOptions.RoleClaim"/>): one of the app's roles when MatIdentity decides the role, null when the app does.</param>
/// <param name="IdToken">The ID token; keep it with the local session, the sign-out at MatIdentity needs it (<see cref="MatIdentitySignIn.SignOutAtIdentity"/>).</param>
/// <param name="Principal">All claims, for anything else.</param>
public sealed record MatIdentityPerson(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role, string? IdToken, ClaimsPrincipal Principal);

/// <summary>What the app answers to a person MatIdentity signed in.</summary>
public sealed record MatIdentitySignInResult(bool Succeeded, string? Error = null)
{
    /// <summary>The key of the local account the person has to prove before it is linked (<see cref="LinkRequired"/>), else null.</summary>
    public string? LinkAccount { get; init; }

    public static MatIdentitySignInResult Success() => new(true);

    public static MatIdentitySignInResult Refused(string error) => new(false, error);

    /// <summary>
    /// No local user has the person's <c>sub</c>, but one unlinked, active account matches by its e-mail (or user name). The address in the app proves
    /// nothing, so the account is not linked and no second one is created: the package keeps a <see cref="MatIdentityPendingLink"/> to
    /// <paramref name="account"/> (the app's key of it) and sends the browser to the local sign-in (<c>LoginPath?local=1&amp;link=1</c>). The app links the
    /// account after its password there (<see cref="MatIdentityPendingLink.TakeFor"/>).
    /// </summary>
    public static MatIdentitySignInResult LinkRequired(string account)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        return new(false) { LinkAccount = account };
    }
}

/// <summary>The app's side of the sign-in through MatIdentity.</summary>
public interface IMatIdentityUsers
{
    /// <summary>
    /// Finds the local user by <see cref="MatIdentityPerson.Subject"/> (else, when one unlinked account has the same e-mail, answers
    /// <see cref="MatIdentitySignInResult.LinkRequired"/>: it is linked only after its password; else creates one), follows
    /// <see cref="MatIdentityPerson.Role"/> when it is set, and signs the user in with the app's own cookie or session (keeping
    /// <see cref="MatIdentityPerson.IdToken"/> for the sign-out), writing it to the activity log. Refused: the person lands on the access-denied page.
    /// </summary>
    Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct);
}

/// <summary>How the sign-in through MatIdentity fits into the app: its cookie scheme and its pages.</summary>
public sealed class MatIdentitySignInOptions
{
    /// <summary>The app's own cookie scheme: the OpenID Connect handler needs a scheme that can sign in (the app signs in itself in <see cref="IMatIdentityUsers"/>).</summary>
    public string CookieScheme { get; set; } = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>
    /// The app's sign-in page (with <c>?local=1</c> the local form, with <c>?signedOut=1</c> no automatic sign-in, <c>?ssoError=1</c> a message,
    /// <c>?link=1</c> the text of a <see cref="MatIdentityPendingLink"/>: sign in once with the password of the account to link it, or cancel).
    /// </summary>
    public string LoginPath { get; set; } = "/Account/Login";

    /// <summary>Where the browser lands after the sign-out at MatIdentity; default: the sign-in page with <c>?signedOut=1</c>.</summary>
    public string? SignedOutRedirect { get; set; }

    /// <summary>Where a failed sign-in at MatIdentity ends; default: the local form with <c>?local=1&amp;ssoError=1</c>.</summary>
    public string? FailedRedirect { get; set; }

    /// <summary>Where a person ends whose account has to be linked first (<see cref="MatIdentitySignInResult.LinkRequired"/>); default: the local form with <c>?local=1&amp;link=1</c>.</summary>
    public string? LinkRedirect { get; set; }

    /// <summary>Where a person ends whom <see cref="IMatIdentityUsers"/> refused.</summary>
    public string AccessDeniedPath { get; set; } = "/AccessDenied";

    /// <summary>The claim that carries the role of the person in this app.</summary>
    public string RoleClaim { get; set; } = "role";

    /// <summary>The scopes asked for.</summary>
    public IList<string> Scopes { get; } = ["openid", "profile", "email", "roles", "offline_access"];

    /// <summary>
    /// Behind a TLS-terminating proxy (Matcad) the scheme of a request comes from <c>X-Forwarded-Proto</c> (<see cref="MatForwardedProto"/>): only the
    /// scheme, never the client address or the host. Default on; off when the app's own ForwardedHeaders policy must be the only one.
    /// </summary>
    public bool TrustForwardedProto { get; set; } = true;

    internal string SignedOutTarget => SignedOutRedirect ?? LoginPath + "?signedOut=1";

    internal string FailedTarget => FailedRedirect ?? LoginPath + "?local=1&ssoError=1";

    internal string LinkTarget => LinkRedirect ?? LoginPath + "?local=1&link=1";
}

/// <summary>
/// The sign-in through MatIdentity (MatDesign docs/PLATFORM.md, "Sign-in with MatIdentity"), on only when <c>MATIDENTITY_AUTHORITY</c> is set:
/// OpenID Connect with the authorization code and PKCE for people (scheme <see cref="Scheme"/>; challenge it from the sign-in page unless
/// <c>?local=1</c>), and the JWT bearer scheme <see cref="MatApiAccess.BearerScheme"/> for the tokens of other apps on the protected part of <c>/api</c>.
/// The person is handed to the app's <see cref="IMatIdentityUsers"/>, which signs the local user in with the app's own cookie.
/// </summary>
public static class MatIdentitySignIn
{
    /// <summary>The OpenID Connect scheme.</summary>
    public const string Scheme = "MatIdentity";

    public const string CallbackPath = "/signin-oidc";

    public const string SignedOutCallbackPath = "/signout-callback-oidc";

    private const string IdTokenHintProperty = "matplatform:id_token_hint";

    /// <summary>Where the ID token of the current callback is kept between token validation and the local sign-in.</summary>
    private const string IdTokenItem = "matplatform:id-token";

    /// <summary>
    /// Adds the sign-in through MatIdentity when <paramref name="settings"/> name it (nothing changes otherwise) and registers
    /// <typeparamref name="TUsers"/> as the app's <see cref="IMatIdentityUsers"/>.
    /// </summary>
    public static AuthenticationBuilder AddMatIdentitySignIn<TUsers>(this AuthenticationBuilder builder, MatPlatformSettings settings, Action<MatIdentitySignInOptions>? configure = null)
        where TUsers : class, IMatIdentityUsers
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);

        var signIn = new MatIdentitySignInOptions();
        configure?.Invoke(signIn);
        builder.Services.AddSingleton(signIn);
        // The pending link (MatIdentityPendingLink) is protected with the app's keys.
        builder.Services.AddDataProtection();
        builder.Services.AddScoped<IMatIdentityUsers, TUsers>();

        if (!settings.IdentityEnabled)
        {
            return builder;
        }

        if (signIn.TrustForwardedProto)
        {
            // Behind Matcad the app speaks http: without the browser's scheme the redirect_uri is http and the correlation cookies are not Secure.
            builder.Services.AddTransient<IStartupFilter, MatForwardedProtoStartupFilter>();
        }

        var authority = new Uri(settings.Authority!);
        // http only for a MatIdentity on http (development); a public one is https and must stay so.
        var requireHttps = !string.Equals(authority.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        builder.AddOpenIdConnect(Scheme, "MatIdentity", options =>
        {
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.RequireHttpsMetadata = requireHttps;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.SaveTokens = false;
            options.MapInboundClaims = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.CallbackPath = CallbackPath;
            options.SignedOutCallbackPath = SignedOutCallbackPath;
            options.SignedOutRedirectUri = signIn.SignedOutTarget;
            // The ticket is never stored by this handler (OnTicketReceived signs in through the app instead), but it needs a
            // scheme that can sign in; the sign-out reads the ID token (id_token_hint) from the app's session.
            options.SignInScheme = signIn.CookieScheme;
            options.SignOutScheme = signIn.CookieScheme;

            options.Scope.Clear();
            foreach (var scope in signIn.Scopes)
            {
                options.Scope.Add(scope);
            }

            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = signIn.RoleClaim;

            if (!requireHttps)
            {
                // A development MatIdentity on http: the correlation and nonce cookies cannot be "Secure; SameSite=None" there.
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
            }

            if (Backchannel(settings) is { } handler)
            {
                options.BackchannelHttpHandler = handler;
            }

            // SaveTokens is off; only the ID token is kept (by the app, with its session), as the id_token_hint of the sign-out.
            options.Events.OnTokenValidated = context =>
            {
                context.HttpContext.Items[IdTokenItem] = context.TokenEndpointResponse?.IdToken ?? context.ProtocolMessage?.IdToken;
                return Task.CompletedTask;
            };
            options.Events.OnTicketReceived = context => SignInLocallyAsync(context, signIn);
            options.Events.OnRedirectToIdentityProviderForSignOut = context =>
            {
                // The session cookie is already gone at this point; the sign-out page hands the ID token over explicitly.
                if (context.Properties.Items.TryGetValue(IdTokenHintProperty, out var hint) && !string.IsNullOrEmpty(hint))
                {
                    context.ProtocolMessage.IdTokenHint = hint;
                }

                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            {
                Logger(context.HttpContext).LogWarning(context.Failure, "Sign-in through MatIdentity failed");
                context.HandleResponse();
                context.Response.Redirect(signIn.FailedTarget);
                return Task.CompletedTask;
            };
        });

        builder.AddJwtBearer(MatApiAccess.BearerScheme, options =>
        {
            options.Authority = settings.Authority;
            options.Audience = settings.ClientId;
            options.RequireHttpsMetadata = requireHttps;
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = signIn.RoleClaim;

            if (Backchannel(settings) is { } handler)
            {
                options.BackchannelHttpHandler = handler;
            }
        });

        return builder;
    }

    /// <summary>
    /// The result of the sign-out page for a person who signed in through MatIdentity (call it after the local sign-out): ends the session at MatIdentity too
    /// (end-session endpoint, <c>id_token_hint</c>, back to <see cref="SignedOutCallbackPath"/> and then <see cref="MatIdentitySignInOptions.SignedOutRedirect"/>).
    /// </summary>
    public static SignOutResult SignOutAtIdentity(HttpContext http, string identityToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrEmpty(identityToken);

        var signIn = http.RequestServices.GetService<MatIdentitySignInOptions>() ?? new MatIdentitySignInOptions();
        var properties = new AuthenticationProperties { RedirectUri = signIn.SignedOutTarget };
        properties.Items[IdTokenHintProperty] = identityToken;
        return new SignOutResult([Scheme], properties);
    }

    /// <summary>The rewriting handler for <c>MATIDENTITY_INTERNAL_URL</c>, or null without it.</summary>
    private static MatIdentityBackchannelHandler? Backchannel(MatPlatformSettings settings)
        => settings.InternalUrl is null
            ? null
            : new MatIdentityBackchannelHandler(new Uri(settings.Authority!), new Uri(settings.InternalUrl));

    /// <summary>MatIdentity has signed the person in: the app finds or creates the local user and signs it in.</summary>
    private static async Task SignInLocallyAsync(TicketReceivedContext context, MatIdentitySignInOptions signIn)
    {
        var http = context.HttpContext;
        var principal = context.Principal ?? new ClaimsPrincipal();

        string? Claim(string type) => principal.FindFirst(type)?.Value;

        var person = new MatIdentityPerson(
            Claim("sub") ?? string.Empty,
            Claim("email"),
            Claim("name"),
            Claim("preferred_username"),
            Claim(signIn.RoleClaim),
            context.Properties?.GetTokenValue("id_token") ?? http.Items[IdTokenItem] as string,
            principal);

        var target = await CompleteSignInAsync(http, person, context.ReturnUri);
        context.HandleResponse();
        context.Response.Redirect(target);
    }

    /// <summary>
    /// What the callback (<see cref="CallbackPath"/>) does once MatIdentity has signed the person in: hands the person to the app's
    /// <see cref="IMatIdentityUsers"/> and answers where the browser goes: <paramref name="returnUrl"/> (when local) or <c>/</c>, the access-denied page
    /// for a refusal, or the local sign-in (<c>?local=1&amp;link=1</c>) with a <see cref="MatIdentityPendingLink"/> stored when the app answered
    /// <see cref="MatIdentitySignInResult.LinkRequired"/>. Public so that an app's tests can run the whole sign-in without a MatIdentity.
    /// </summary>
    public static async Task<string> CompleteSignInAsync(HttpContext http, MatIdentityPerson person, string? returnUrl = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(person);

        var signIn = http.RequestServices.GetService<MatIdentitySignInOptions>() ?? new MatIdentitySignInOptions();
        var result = await http.RequestServices.GetRequiredService<IMatIdentityUsers>().SignInAsync(person, http, http.RequestAborted);

        if (result.LinkAccount is { } account)
        {
            // Not linked by the address alone: the person proves the account with its password first.
            MatIdentityPendingLink.Store(http, person, account);
            Logger(http).LogInformation("{Subject} signed in through MatIdentity; the matching account has to be linked by its password first", person.Subject);
            return signIn.LinkTarget;
        }

        // Whatever was pending in this browser is settled by this sign-in.
        MatIdentityPendingLink.Clear(http);

        if (!result.Succeeded)
        {
            Logger(http).LogWarning("Sign-in through MatIdentity refused for {Subject}: {Error}", person.Subject, result.Error);
            return signIn.AccessDeniedPath;
        }

        Logger(http).LogInformation("{Subject} signed in through MatIdentity", person.Subject);
        return !string.IsNullOrEmpty(returnUrl) && IsLocal(returnUrl) ? returnUrl : "/";
    }

    private static ILogger Logger(HttpContext http)
        => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MatIdentitySignIn));

    private static bool IsLocal(string url)
        => url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal);
}
