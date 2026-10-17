// MatPlatform 1.2.0 – a copy from MatDesign, do not edit here
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MatPlatform;

/// <summary>
/// A person MatIdentity signed in whose address matches a local account that is not linked yet (docs/PLATFORM.md, "The person"). The address in the app
/// proves nothing (anyone may have typed it into their own profile), so the account is linked only after its password: the person signs in locally once
/// (<c>LoginPath?local=1&amp;link=1</c>) and the app links the account when it is the one that matched (<see cref="TakeFor"/>).
/// Kept for <see cref="Lifetime"/> in a cookie of the app, protected with ASP.NET Data Protection (HttpOnly, Secure on https, SameSite=Lax).
/// </summary>
/// <param name="Subject">The claim <c>sub</c>: the account gets it as its external subject when it is linked.</param>
/// <param name="Email">The claim <c>email</c>.</param>
/// <param name="Name">The claim <c>name</c>.</param>
/// <param name="PreferredUserName">The claim <c>preferred_username</c>.</param>
/// <param name="Role">The role claim, applied like at a sign-in through MatIdentity when the account is linked.</param>
/// <param name="IdToken">The ID token, to keep with the session for the sign-out at MatIdentity.</param>
/// <param name="Account">The key of the local account that matched (the app's own id), the only account that may be linked.</param>
public sealed record MatIdentityPendingLink(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role, string? IdToken, string Account)
{
    /// <summary>The name of the cookie.</summary>
    public const string CookieName = ".MatPlatform.PendingLink";

    /// <summary>How long the person has to sign in locally.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The purpose of the Data Protection keys (another purpose or app cannot read the cookie).</summary>
    public const string Purpose = "MatPlatform.MatIdentityPendingLink.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Keeps the person as the pending link to <paramref name="account"/> (replacing an earlier one).</summary>
    public static void Store(HttpContext http, MatIdentityPerson person, string account)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(person);
        ArgumentException.ThrowIfNullOrEmpty(person.Subject);
        ArgumentException.ThrowIfNullOrEmpty(account);

        var link = new MatIdentityPendingLink(person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role, person.IdToken, account);
        var value = Protector(http).Protect(JsonSerializer.Serialize(link, Json), Lifetime);
        http.Response.Cookies.Append(CookieName, value, Options(http, DateTimeOffset.UtcNow.Add(Lifetime)));
    }

    /// <summary>The pending link of this browser, or null (none, expired, forged or from another app); leaves it in place (for the sign-in page's text).</summary>
    public static MatIdentityPendingLink? Peek(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!http.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var link = JsonSerializer.Deserialize<MatIdentityPendingLink>(Protector(http).Unprotect(value), Json);
            return link is null || string.IsNullOrEmpty(link.Subject) || string.IsNullOrEmpty(link.Account) ? null : link;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The pending link of this browser (or null), deleting it.</summary>
    public static MatIdentityPendingLink? Take(HttpContext http)
    {
        var link = Peek(http);
        Clear(http);
        return link;
    }

    /// <summary>
    /// After a successful local sign-in (with the app's second factor, when it has one): the pending link when it was made for exactly
    /// <paramref name="account"/>, else null. The link is deleted either way: another account never gets it.
    /// </summary>
    public static MatIdentityPendingLink? TakeFor(HttpContext http, string account)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        var link = Take(http);
        return link is not null && string.Equals(link.Account, account, StringComparison.Ordinal) ? link : null;
    }

    /// <summary>Deletes the pending link (cancel, sign-out, a sign-in through MatIdentity that needs none).</summary>
    public static void Clear(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (http.Request.Cookies.ContainsKey(CookieName))
        {
            http.Response.Cookies.Delete(CookieName, Options(http, null));
        }
    }

    private static ITimeLimitedDataProtector Protector(HttpContext http)
        => http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).ToTimeLimitedDataProtector();

    private static CookieOptions Options(HttpContext http, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = http.Request.PathBase.HasValue ? http.Request.PathBase.Value : "/",
        Expires = expires,
    };
}
