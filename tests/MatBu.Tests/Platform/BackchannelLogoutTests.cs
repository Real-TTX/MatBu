using System.Net;
using System.Text.RegularExpressions;
using MatBu.Data;
using MatPlatform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MatBu.Tests.Platform;

/// <summary>
/// The back-channel logout of MatIdentity (MatPlatform 1.4.0, <c>POST /signout-backchannel</c>): a signed logout token deletes the sessions it names,
/// so the next request with such a cookie is signed out.
/// </summary>
public sealed partial class BackchannelLogoutTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    private const string Path = "/signout-backchannel";

    [Fact]
    public async Task A_valid_token_ends_the_session_it_names_and_only_that_one()
    {
        var ended = await SignInAsync("sub-bc-1", "sid-bc-1a");
        var other = await SignInAsync("sub-bc-1", "sid-bc-1b");
        Assert.Single(Store().Read().UserSessions, s => s.SessionId == "sid-bc-1a");
        Assert.Equal(HttpStatusCode.OK, (await ended.GetAsync("/")).StatusCode);

        var response = await PostAsync(Token("sub-bc-1", "sid-bc-1a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await ended.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ended.GetAsync("/api/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task A_token_with_the_subject_alone_ends_every_session_of_the_person()
    {
        var first = await SignInAsync("sub-bc-2", "sid-bc-2a");
        var second = await SignInAsync("sub-bc-2", "sid-bc-2b");
        var stranger = await SignInAsync("sub-bc-3", "sid-bc-3");

        var response = await PostAsync(Token("sub-bc-2", sessionId: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await first.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_person_or_session_is_no_error()
    {
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-nobody", "sid-nobody"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-nobody", sessionId: null))).StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_400_and_ends_nothing()
    {
        var browser = await SignInAsync("sub-bc-4", "sid-bc-4");
        var forger = new SigningCredentials(new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Token("sub-bc-4", "sid-bc-4", forger))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task A_session_keeps_its_sid_and_a_token_with_the_sid_alone_ends_it()
    {
        var accounts = site.Services.GetRequiredService<MatBu.Services.UserAccountService>();
        var token = accounts.CreateSession(1, "id-token", "sid-bc-5");
        Assert.Equal("sid-bc-5", Assert.Single(Store().Read().UserSessions, s => s.Token == token).SessionId);
        Assert.Equal(1, accounts.EndExternalSessions(null, "sid-bc-5"));
        Assert.DoesNotContain(Store().Read().UserSessions, s => s.Token == token);
    }

    private PersistentStore Store() => site.Services.GetRequiredService<PersistentStore>();

    /// <summary>Signs the person in through MatBu's <see cref="IMatIdentityUsers"/> and answers a browser with that session cookie.</summary>
    private async Task<HttpClient> SignInAsync(string subject, string sessionId)
    {
        using var scope = site.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var person = new MatIdentityPerson(subject, subject + "@example.com", null, null, "user", "id-token-" + sessionId, new()) { SessionId = sessionId };
        var result = await scope.ServiceProvider.GetRequiredService<IMatIdentityUsers>().SignInAsync(person, http, CancellationToken.None);
        Assert.True(result.Succeeded);

        var token = SessionRegex().Match(http.Response.Headers.SetCookie.ToString()).Groups[1].Value;
        var cookies = new CookieContainer();
        cookies.Add(new Uri("http://localhost"), new Cookie(MatBu.Services.UserAccountService.SessionCookie, Uri.UnescapeDataString(token)));
        return site.Browser(cookies);
    }

    private Task<HttpResponseMessage> PostAsync(string token)
        => site.CreateClient().PostAsync(Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));

    /// <summary>A logout token as MatIdentity writes it (OpenID Connect Back-Channel Logout 1.0).</summary>
    internal static string Token(string? subject, string? sessionId, SigningCredentials? credentials = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = IdentityFactory.Authority,
            ["aud"] = "matbu",
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 120,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["events"] = new Dictionary<string, object> { [MatIdentityLogoutToken.EventType] = new Dictionary<string, object>() },
        };
        if (subject is not null) claims["sub"] = subject;
        if (sessionId is not null) claims["sid"] = sessionId;

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            TokenType = MatIdentityLogoutToken.TokenType,
            SigningCredentials = credentials ?? MatBuFactory.IdentityCredentials,
        });
    }

    [GeneratedRegex("matbu_session=([^;]+)")]
    private static partial Regex SessionRegex();
}

/// <summary>Without <c>MATIDENTITY_AUTHORITY</c> there is no back-channel logout.</summary>
public sealed class BackchannelLogoutWithoutIdentityTests(MatBuFactory site) : IClassFixture<MatBuFactory>
{
    [Fact]
    public async Task Without_matidentity_there_is_no_endpoint()
    {
        var response = await site.CreateClient().PostAsync("/signout-backchannel", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["logout_token"] = BackchannelLogoutTests.Token("sub-x", "sid-x"),
        }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
