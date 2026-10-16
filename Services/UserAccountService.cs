using System.Security.Cryptography;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

/// <summary>A person signed in by MatIdentity, as MatBu needs it to find or create its local user.</summary>
/// <param name="Subject">The claim <c>sub</c>, the stable id of the person.</param>
/// <param name="Email">The claim <c>email</c>: links the local user with this user name at the first sign-in.</param>
/// <param name="Name">The claim <c>name</c>.</param>
/// <param name="PreferredUserName">The claim <c>preferred_username</c>.</param>
/// <param name="Role">The claim <c>role</c> (<c>admin</c>, <c>operator</c>, <c>user</c>) when MatIdentity decides the role, else null.</param>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role);

/// <summary>The outcome of <see cref="UserAccountService.SignInExternal"/>.</summary>
public sealed record ExternalSignInResult(AppUser? User, string? Error)
{
    public bool Succeeded => User is not null;
}

/// <summary>
/// MatBu's users and sessions: the session cookie <see cref="SessionCookie"/> and the sign-in of a person that MatIdentity signed in
/// (matched by <see cref="AppUser.ExternalSubject"/>, else by an e-mail address that is the local user name, else created).
/// </summary>
public sealed class UserAccountService(PersistentStore store)
{
    /// <summary>The cookie that carries the token of a <see cref="UserSession"/>.</summary>
    public const string SessionCookie = "matbu_session";

    /// <summary>How long a session lasts.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    /// <summary>The local role of a value of the claim <c>role</c>; null for an unknown value (then the role stays as it is).</summary>
    public static UserRole? RoleOf(string? claim) => claim?.Trim().ToLowerInvariant() switch
    {
        "admin" => UserRole.Admin,
        "operator" => UserRole.Operator,
        "user" => UserRole.User,
        _ => null
    };

    /// <summary>
    /// Finds the local user of <paramref name="identity"/>: by the subject, else the not yet linked user whose user name is the e-mail address
    /// (then it is linked), else a new user (without a usable password: the way in is MatIdentity). The role follows the claim <c>role</c> when it is
    /// one of MatBu's roles; a new user without it gets the least role.
    /// </summary>
    public ExternalSignInResult SignInExternal(ExternalIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Subject)) return new ExternalSignInResult(null, "MatIdentity did not send a subject.");

        var role = RoleOf(identity.Role);
        AppUser? result = null;
        store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            var user = data.Users.FirstOrDefault(item => item.ExternalSubject == identity.Subject);
            if (user is null && !string.IsNullOrWhiteSpace(identity.Email))
            {
                user = data.Users.FirstOrDefault(item =>
                    string.IsNullOrEmpty(item.ExternalSubject) &&
                    item.UserName.Equals(identity.Email.Trim(), StringComparison.OrdinalIgnoreCase));
                if (user is not null)
                {
                    user.ExternalSubject = identity.Subject;
                    user.UpdateDate = now;
                }
            }

            if (user is null)
            {
                user = new AppUser
                {
                    Id = store.NextId(data.Users.Select(item => item.Id)),
                    UserName = UniqueUserName(data.Users, identity),
                    PasswordHash = PersistentStore.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
                    Role = role ?? UserRole.User,
                    ExternalSubject = identity.Subject,
                    CreateDate = now,
                    UpdateDate = now
                };
                data.Users.Add(user);
            }
            else if (role is { } claimed && user.Role != claimed)
            {
                user.Role = claimed;
                user.UpdateDate = now;
            }

            result = user;
        });

        return new ExternalSignInResult(result, null);
    }

    /// <summary>Starts a session of <paramref name="userId"/> and answers its token (the value of <see cref="SessionCookie"/>).</summary>
    public string CreateSession(long userId, string? idToken = null)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            data.UserSessions.Add(new UserSession
            {
                Id = store.NextId(data.UserSessions.Select(item => item.Id)),
                Token = token,
                UserId = userId,
                ExpiresDate = now.Add(SessionLifetime),
                IdToken = string.IsNullOrEmpty(idToken) ? null : idToken,
                CreateDate = now,
                UpdateDate = now
            });
        });
        return token;
    }

    /// <summary>The ID token of MatIdentity kept with the session, or null (a local sign-in, or no session).</summary>
    public string? GetIdToken(string? sessionToken) =>
        string.IsNullOrWhiteSpace(sessionToken)
            ? null
            : store.Read().UserSessions.FirstOrDefault(item => item.Token == sessionToken)?.IdToken;

    /// <summary>Ends the session with this token.</summary>
    public void EndSession(string? sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return;
        store.Update(data => data.UserSessions.RemoveAll(item => item.Token == sessionToken));
    }

    private static string UniqueUserName(IReadOnlyCollection<AppUser> users, ExternalIdentity identity)
    {
        var name = new[] { identity.Email, identity.PreferredUserName, identity.Name }
            .Select(value => value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrEmpty(value))
            ?? "matidentity-" + identity.Subject[..Math.Min(8, identity.Subject.Length)];
        var candidate = name;
        for (var suffix = 2; users.Any(item => item.UserName.Equals(candidate, StringComparison.OrdinalIgnoreCase)); suffix++)
            candidate = $"{name}-{suffix}";
        return candidate;
    }
}
