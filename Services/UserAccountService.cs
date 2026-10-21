using System.Security.Cryptography;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

/// <summary>A person signed in by MatIdentity, as MatBu needs it to find or create its local user.</summary>
/// <param name="Subject">The claim <c>sub</c>, the stable id of the person.</param>
/// <param name="Email">The claim <c>email</c>: the local user with this user name is linked only after its password (<see cref="UserAccountService.LinkExternal"/>).</param>
/// <param name="Name">The claim <c>name</c>.</param>
/// <param name="PreferredUserName">The claim <c>preferred_username</c>.</param>
/// <param name="Role">The claim <c>role</c> (<c>admin</c>, <c>operator</c>, <c>user</c>) when MatIdentity decides the role, else null.</param>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role);

/// <summary>The outcome of <see cref="UserAccountService.SignInExternal"/> and <see cref="UserAccountService.LinkExternal"/>.</summary>
public sealed record ExternalSignInResult(AppUser? User, string? Error)
{
    public bool Succeeded => User is not null;

    /// <summary>
    /// The id of the not yet linked local user whose user name is the e-mail address: it is not linked by the address alone (anyone can create a
    /// user with any name), the person has to sign in locally with its password first.
    /// </summary>
    public long? LinkUserId { get; init; }
}

/// <summary>
/// MatBu's users and sessions: the session cookie <see cref="SessionCookie"/> and the sign-in of a person that MatIdentity signed in
/// (matched by <see cref="AppUser.ExternalSubject"/>, else created; a local user whose name is the e-mail address is linked only after its password).
/// </summary>
public sealed class UserAccountService(PersistentStore store, ILogger<UserAccountService> logger)
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
    /// Finds the local user of <paramref name="identity"/>: by the subject, else a new user (without a usable password: the way in is MatIdentity).
    /// When the one not yet linked user's name is the e-mail address, nothing is changed and nothing is created: the result names it in
    /// <see cref="ExternalSignInResult.LinkUserId"/>, and it is linked by <see cref="LinkExternal"/> once its password has been given. The role follows
    /// the claim <c>role</c> when it is one of MatBu's roles; a new user without it gets the least role.
    /// </summary>
    public ExternalSignInResult SignInExternal(ExternalIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Subject)) return new ExternalSignInResult(null, "MatIdentity did not send a subject.");

        var role = RoleOf(identity.Role);
        AppUser? result = null;
        long? link = null;
        store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            var user = data.Users.FirstOrDefault(item => item.ExternalSubject == identity.Subject);
            if (user is null && MatchByEmail(data.Users, identity.Email) is { } match)
            {
                // The user name proves nothing about the person: the account is linked only after its password (LinkExternal).
                link = match.Id;
                return;
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

        return link is { } id ? new ExternalSignInResult(null, null) { LinkUserId = id } : new ExternalSignInResult(result, null);
    }

    /// <summary>
    /// Links the local user <paramref name="userId"/> with the person MatIdentity signed in, after that user has just signed in locally with its
    /// password (the proof that the account belongs to the person). Only while it is still the one not yet linked user whose name is the e-mail
    /// address and no other user has the subject. Sets the subject, follows the claim <c>role</c> like <see cref="SignInExternal"/> and logs it.
    /// </summary>
    public ExternalSignInResult LinkExternal(long userId, ExternalIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Subject)) return new ExternalSignInResult(null, "MatIdentity did not send a subject.");

        var role = RoleOf(identity.Role);
        AppUser? result = null;
        string? error = null;
        store.Update(data =>
        {
            if (data.Users.Any(item => item.ExternalSubject == identity.Subject))
            {
                error = "Another user is already linked with this MatIdentity account.";
                return;
            }

            var user = MatchByEmail(data.Users, identity.Email);
            if (user is null || user.Id != userId)
            {
                error = "The user is no longer the one that matched.";
                return;
            }

            user.ExternalSubject = identity.Subject;
            if (role is { } claimed) user.Role = claimed;
            user.UpdateDate = DateTimeOffset.UtcNow;
            result = user;
        });

        if (result is null)
        {
            logger.LogWarning("User {UserId} was not linked with MatIdentity ({Subject}): {Error}", userId, identity.Subject, error);
            return new ExternalSignInResult(null, error);
        }

        logger.LogInformation("{UserName} ({Role}) linked with MatIdentity ({Subject}) after the local sign-in", result.UserName, result.Role, identity.Subject);
        return new ExternalSignInResult(result, null);
    }

    /// <summary>The user with this name and password, or null.</summary>
    public AppUser? FindByPassword(string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)) return null;
        var user = store.Read().Users.FirstOrDefault(item => item.UserName.Equals(userName.Trim(), StringComparison.OrdinalIgnoreCase));
        return user is not null && PersistentStore.VerifyPassword(password, user.PasswordHash) ? user : null;
    }

    /// <summary>Starts a session of <paramref name="userId"/> and answers its token (the value of <see cref="SessionCookie"/>).</summary>
    /// <param name="userId">The local user.</param>
    /// <param name="idToken">The ID token of a sign-in through MatIdentity (for the sign-out there), else null.</param>
    /// <param name="sessionId">The claim <c>sid</c> of that sign-in (its session at MatIdentity, for the back-channel logout), else null.</param>
    public string CreateSession(long userId, string? idToken = null, string? sessionId = null)
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
                SessionId = string.IsNullOrEmpty(sessionId) ? null : sessionId,
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

    /// <summary>
    /// Ends the sessions MatIdentity signed out (back-channel logout): with <paramref name="sessionId"/> the sessions started with that <c>sid</c>
    /// (of the user linked to <paramref name="subject"/> when it is given), with <paramref name="subject"/> alone every session of the user linked
    /// to it. The rows are deleted, so the next request with such a cookie is signed out. An unknown subject or sid ends nothing. Answers the count.
    /// </summary>
    public int EndExternalSessions(string? subject, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(subject) && string.IsNullOrWhiteSpace(sessionId)) return 0;

        var ended = 0;
        string? userName = null;
        store.Update(data =>
        {
            var user = string.IsNullOrWhiteSpace(subject) ? null : data.Users.FirstOrDefault(item => item.ExternalSubject == subject);
            if (!string.IsNullOrWhiteSpace(subject) && user is null) return;
            userName = user?.UserName;
            ended = string.IsNullOrWhiteSpace(sessionId)
                ? data.UserSessions.RemoveAll(item => item.UserId == user!.Id)
                : data.UserSessions.RemoveAll(item => item.SessionId == sessionId && (user is null || item.UserId == user.Id));
        });

        if (ended > 0)
            logger.LogInformation("MatIdentity signed out {UserName} ({Subject}, sid {SessionId}): {Count} session(s) ended", userName ?? "a user", subject, sessionId, ended);
        else
            logger.LogInformation("MatIdentity signed out {Subject} (sid {SessionId}): no session of MatBu to end", subject, sessionId);

        return ended;
    }

    /// <summary>The one not yet linked user whose user name is <paramref name="email"/>, or null.</summary>
    private static AppUser? MatchByEmail(IEnumerable<AppUser> users, string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var matches = users.Where(item => string.IsNullOrEmpty(item.ExternalSubject) && item.UserName.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
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
