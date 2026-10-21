using System.Globalization;
using MatBu.Services;
using MatPlatform;

namespace MatBu.Platform;

/// <summary>
/// MatBu's side of the sign-in through MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>): the person is mapped to a local user by
/// <see cref="UserAccountService.SignInExternal"/> (by <c>sub</c>, else a new user; the claim <c>role</c> when it is there) and then signed in with
/// MatBu's own session cookie, like a local sign-in, keeping the ID token with the session for the sign-out at MatIdentity. A not yet linked user
/// whose name is the e-mail address is not entered: the answer is <see cref="MatIdentitySignInResult.LinkRequired"/>, the person signs in once with
/// that user's password on <c>/Login?local=1&amp;link=1</c> and the login page links it (<see cref="UserAccountService.LinkExternal"/>).
/// The back-channel logout of MatIdentity (<see cref="IMatIdentitySessions"/>, <c>POST /signout-backchannel</c>) deletes the sessions it names
/// (<see cref="UserAccountService.EndExternalSessions"/>), so the next request with such a cookie is signed out.
/// </summary>
public sealed class MatBuIdentityUsers(UserAccountService accounts, ILogger<MatBuIdentityUsers> logger) : IMatIdentityUsers, IMatIdentitySessions
{
    public Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        var result = accounts.SignInExternal(new ExternalIdentity(person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role));
        if (result.LinkUserId is { } linkUserId)
        {
            return Task.FromResult(MatIdentitySignInResult.LinkRequired(linkUserId.ToString(CultureInfo.InvariantCulture)));
        }

        if (!result.Succeeded)
        {
            return Task.FromResult(MatIdentitySignInResult.Refused(result.Error ?? "refused"));
        }

        var user = result.User!;
        var token = accounts.CreateSession(user.Id, person.IdToken, person.SessionId);

        // Lax, not Strict like the local sign-in: this response ends a cross-site redirect chain (MatIdentity → /signin-oidc → the page),
        // and a Strict cookie would not be sent on the next step, so the page would send the browser back to MatIdentity.
        http.Response.Cookies.Append(UserAccountService.SessionCookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            MaxAge = UserAccountService.SessionLifetime
        });

        logger.LogInformation("{UserName} ({Role}) signed in through MatIdentity", user.UserName, user.Role);
        return Task.FromResult(MatIdentitySignInResult.Success());
    }

    /// <inheritdoc />
    public Task EndSessionsAsync(string? subject, string? sessionId, CancellationToken ct)
    {
        accounts.EndExternalSessions(subject, sessionId);
        return Task.CompletedTask;
    }
}
