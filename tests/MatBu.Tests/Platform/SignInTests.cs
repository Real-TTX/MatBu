using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatBu.Data;
using MatBu.Models;
using MatBu.Platform;
using MatBu.Services;
using MatPlatform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace MatBu.Tests.Platform;

/// <summary>Helpers of the sign-in tests.</summary>
internal static partial class SignIn
{
    /// <summary>Posts the local form of <paramref name="page"/> (the page was just loaded by <paramref name="browser"/>).</summary>
    public static async Task<HttpResponseMessage> PostLocalFormAsync(HttpClient browser, string url, string page, string user = "admin", string password = "admin")
        => await browser.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(page),
            ["UserName"] = user,
            ["Password"] = password,
        }));

    public static string AntiforgeryToken(string html) => TokenRegex().Match(html).Groups[1].Value;

    public static string Path(HttpResponseMessage response) => response.Headers.Location!.OriginalString.Replace("http://localhost", string.Empty);

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex TokenRegex();
}

/// <summary>Without <c>MATIDENTITY_AUTHORITY</c> nothing changes: the local sign-in form, as before.</summary>
public sealed class LocalSignInTests(MatBuFactory site) : IClassFixture<MatBuFactory>
{
    [Fact]
    public async Task The_sign_in_page_is_the_local_form_and_it_works()
    {
        var browser = site.Browser();
        var unsigned = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, unsigned.StatusCode);
        Assert.StartsWith("/Login", SignIn.Path(unsigned));

        var page = await browser.GetAsync("/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.DoesNotContain("MatIdentity", html);

        var signedIn = await SignIn.PostLocalFormAsync(browser, "/Login", html);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("matbu_session=", StringComparison.Ordinal) && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task There_is_no_matidentity_callback()
        => Assert.Equal(HttpStatusCode.NotFound, (await site.Browser().GetAsync("/signin-oidc")).StatusCode);
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c>: the sign-in page goes straight to MatIdentity, with the authorization code and PKCE.</summary>
public sealed class IdentityAutoSignInTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [Fact]
    public async Task The_sign_in_page_goes_straight_to_matidentity_with_code_and_pkce()
    {
        var response = await site.Browser().GetAsync("/Login");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/authorize", location.OriginalString);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("matbu", query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        Assert.EndsWith("/signin-oidc", query["redirect_uri"].ToString());
        Assert.Equal(["openid", "profile", "email", "roles", "offline_access"], query["scope"].ToString().Split(' '));
    }

    [Fact]
    public async Task A_page_that_needs_a_sign_in_ends_at_matidentity()
    {
        var browser = site.Browser();
        var first = await browser.GetAsync("/Jobs");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var login = SignIn.Path(first);
        Assert.StartsWith("/Login", login);

        var second = await browser.GetAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.StartsWith(IdentityFactory.Authority, second.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_definition_names_matidentity_and_a_bad_token_is_401()
    {
        using var doc = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api"));
        Assert.Equal(IdentityFactory.Authority, doc.RootElement.GetProperty("auth").GetProperty("identity").GetString());

        var bearer = site.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Api().GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task The_machine_endpoints_stay_on_their_tokens()
    {
        // The secondaries and monitoring keep their own headers; MatIdentity changes nothing there.
        var client = site.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/secondary/poll", null)).StatusCode);
        using var monitoring = new HttpRequestMessage(HttpMethod.Get, "/api/monitoring/health");
        monitoring.Headers.Add("X-MatBu-Token", site.Services.GetRequiredService<PersistentStore>().GetMonitoringToken());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(monitoring)).StatusCode);
    }
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c>: the local way in with <c>?local=1</c> and after the sign-out with <c>?signedOut=1</c>.</summary>
public sealed class IdentityLocalSignInTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [Fact]
    public async Task The_local_form_stays_reachable_and_works()
    {
        var browser = site.Browser();
        var page = await browser.GetAsync("/Login?local=1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("Mit MatIdentity anmelden", html);

        var signedIn = await SignIn.PostLocalFormAsync(browser, "/Login?local=1", html);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task After_the_sign_out_there_is_no_automatic_sign_in()
    {
        var response = await site.Browser().GetAsync("/Login?signedOut=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Mit MatIdentity anmelden", html);
        Assert.DoesNotContain("id=\"login-sso-error\"", html);
    }
}

/// <summary>The person MatIdentity signed in: matched, linked or created as a local user, with the role of the claim, and signed out there too.</summary>
public sealed class IdentityUsersTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [Fact]
    public async Task A_new_person_becomes_a_user_with_the_role_of_the_claim_and_gets_a_lax_session()
    {
        var (result, cookie) = await SignInAsync(new MatIdentityPerson("sub-new", "new@example.com", "New Person", "new", "operator", "id-token-new", new()));
        Assert.True(result.Succeeded);
        Assert.Contains("matbu_session=", cookie);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        var data = Store().Read();
        var user = Assert.Single(data.Users, u => u.ExternalSubject == "sub-new");
        Assert.Equal("new@example.com", user.UserName);
        Assert.Equal(UserRole.Operator, user.Role);
        Assert.Equal("id-token-new", Assert.Single(data.UserSessions, s => s.UserId == user.Id).IdToken);

        // The next sign-in finds the same user by the subject and follows a changed role.
        await SignInAsync(new MatIdentityPerson("sub-new", "changed@example.com", "New Person", "new", "admin", "id-token-2", new()));
        user = Assert.Single(Store().Read().Users, u => u.ExternalSubject == "sub-new");
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Equal("new@example.com", user.UserName);
    }

    [Fact]
    public async Task An_existing_local_user_with_the_e_mail_as_name_is_linked_and_keeps_its_role_without_a_claim()
    {
        site.Update(data => data.Users.Add(new AppUser { Id = 9001, UserName = "Linked@Example.com", PasswordHash = PersistentStore.HashPassword("pw"), Role = UserRole.Admin, CreateDate = DateTimeOffset.UtcNow, UpdateDate = DateTimeOffset.UtcNow }));

        var (result, _) = await SignInAsync(new MatIdentityPerson("sub-linked", "linked@example.com", null, null, null, null, new()));
        Assert.True(result.Succeeded);

        var user = Assert.Single(Store().Read().Users, u => u.Id == 9001);
        Assert.Equal("sub-linked", user.ExternalSubject);
        Assert.Equal(UserRole.Admin, user.Role);
    }

    [Fact]
    public async Task A_person_without_a_role_claim_gets_the_least_role_and_a_unique_name()
    {
        var (result, _) = await SignInAsync(new MatIdentityPerson("sub-admin-name", null, null, "admin", null, null, new()));
        Assert.True(result.Succeeded);
        var user = Assert.Single(Store().Read().Users, u => u.ExternalSubject == "sub-admin-name");
        Assert.Equal(UserRole.User, user.Role);
        Assert.Equal("admin-2", user.UserName);
        Assert.Null(Assert.Single(Store().Read().Users, u => u.UserName == "admin").ExternalSubject);
    }

    [Fact]
    public async Task A_person_without_a_subject_is_refused()
    {
        var (result, _) = await SignInAsync(new MatIdentityPerson("", "x@example.com", null, null, null, null, new()));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task The_session_of_matidentity_opens_the_ui_and_the_sign_out_ends_it_at_matidentity()
    {
        var (_, cookie) = await SignInAsync(new MatIdentityPerson("sub-out", "out@example.com", null, null, "user", "id-token-out", new()));
        var token = Regex.Match(cookie, "matbu_session=([^;]+)").Groups[1].Value;

        var cookies = new CookieContainer();
        cookies.Add(new Uri("http://localhost"), new Cookie("matbu_session", Uri.UnescapeDataString(token)));
        var browser = site.Browser(cookies);

        var home = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = await home.Content.ReadAsStringAsync();

        var signOut = await browser.PostAsync("/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
        }));
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        var location = signOut.Headers.Location!;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/logout", location.OriginalString);
        Assert.Equal("id-token-out", QueryHelpers.ParseQuery(location.Query)["id_token_hint"].ToString());
        Assert.DoesNotContain(Store().Read().UserSessions, s => s.IdToken == "id-token-out");
    }

    private PersistentStore Store() => site.Services.GetRequiredService<PersistentStore>();

    private async Task<(MatIdentitySignInResult Result, string Cookie)> SignInAsync(MatIdentityPerson person)
    {
        using var scope = site.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var result = await scope.ServiceProvider.GetRequiredService<IMatIdentityUsers>().SignInAsync(person, http, CancellationToken.None);
        Assert.IsType<MatBuIdentityUsers>(scope.ServiceProvider.GetRequiredService<IMatIdentityUsers>());
        return (result, http.Response.Headers.SetCookie.ToString());
    }
}

/// <summary>MatIdentity is configured but down: the sign-in page falls back to the local form instead of an error.</summary>
public sealed class UnreachableIdentityTests(UnreachableIdentityFactory site) : IClassFixture<UnreachableIdentityFactory>
{
    [Fact]
    public async Task The_sign_in_page_shows_the_local_form_with_a_message()
    {
        var response = await site.Browser().GetAsync("/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("id=\"login-sso-error\"", html);
    }
}
