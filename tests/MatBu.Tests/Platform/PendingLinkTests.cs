using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatBu.Data;
using MatBu.Models;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MatBu.Tests.Platform;

/// <summary>
/// A local user whose name is the e-mail address of a person MatIdentity signed in is linked only after its password: anyone can create a user with any
/// name, so an attacker could otherwise pre-claim a victim's address and receive the victim at the first sign-in through MatIdentity.
/// Each test starts its own app: the sign-in page allows five requests a minute.
/// </summary>
public sealed partial class PendingLinkTests
{
    private const long VictimId = 9100;
    private const string VictimAddress = "victim@example.com";
    private const string AttackerPassword = "attacker-knows-this";

    [Fact]
    public async Task A_pre_claimed_address_is_not_entered_and_the_browser_lands_on_the_local_sign_in()
    {
        using var site = Site();
        var users = Store(site).Read().Users.Count;

        var (target, setCookie, browser) = await MatIdentitySignInAsync(site);

        Assert.Equal("/Login?local=1&link=1", target);
        Assert.DoesNotContain("matbu_session=", setCookie);
        var data = Store(site).Read();
        Assert.Null(Victim(site).ExternalSubject);
        Assert.Equal(users, data.Users.Count);
        Assert.DoesNotContain(data.UserSessions, s => s.UserId == VictimId);

        var page = await browser.GetAsync(target);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-link\"", html);
        Assert.Contains("Victim@Example.com", System.Net.WebUtility.HtmlDecode(html));
        Assert.Contains("id=\"login-link-cancel\"", html);
    }

    [Fact]
    public async Task The_local_sign_in_of_the_matched_user_links_it_with_the_role_and_the_id_token()
    {
        using var site = Site();
        var (target, _, browser) = await MatIdentitySignInAsync(site);
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        // A wrong password links nothing and keeps the pending link.
        var wrong = await SignIn.PostLocalFormAsync(browser, target, html, VictimAddress, "wrong");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        html = await wrong.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-link\"", html);
        Assert.Null(Victim(site).ExternalSubject);

        var signedIn = await SignIn.PostLocalFormAsync(browser, target, html, VictimAddress, AttackerPassword);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        var victim = Victim(site);
        Assert.Equal("sub-victim", victim.ExternalSubject);
        Assert.Equal(UserRole.Admin, victim.Role);
        Assert.Equal("id-token-victim", Assert.Single(Store(site).Read().UserSessions, s => s.UserId == VictimId).IdToken);

        // From now on MatIdentity finds the user by its subject.
        using var scope = site.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        Assert.Equal("/", await MatIdentitySignIn.CompleteSignInAsync(http, Person(), "/"));
        Assert.Contains("matbu_session=", http.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task Another_user_signing_in_links_nothing_and_drops_the_pending_link()
    {
        using var site = Site();
        var (target, _, browser) = await MatIdentitySignInAsync(site);
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        var signedIn = await SignIn.PostLocalFormAsync(browser, target, html, "admin", "admin");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        var data = Store(site).Read();
        Assert.Null(Victim(site).ExternalSubject);
        Assert.DoesNotContain(data.Users, u => u.ExternalSubject == "sub-victim");
        var admin = Assert.Single(data.Users, u => u.UserName == "admin");
        Assert.Null(Assert.Single(data.UserSessions, s => s.UserId == admin.Id).IdToken);
    }

    [Fact]
    public async Task Cancel_deletes_the_pending_link_and_a_later_sign_in_links_nothing()
    {
        using var site = Site();
        var (target, _, browser) = await MatIdentitySignInAsync(site);
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        var cancelled = await browser.PostAsync("/Login?handler=CancelLink", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
        }));
        Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
        Assert.Equal("/Login?local=1", SignIn.Path(cancelled));
        Assert.Contains(cancelled.Headers.GetValues("Set-Cookie"), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"login-link\"", html);

        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, target, html, VictimAddress, AttackerPassword)).StatusCode);
        Assert.Null(Victim(site).ExternalSubject);
    }

    [Fact]
    public async Task A_forged_pending_cookie_is_ignored()
    {
        using var site = Site();
        var json = JsonSerializer.Serialize(new MatIdentityPendingLink("sub-forged", VictimAddress, null, null, "admin", null, VictimId.ToString()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await AssertIgnoredAsync(site, Uri.EscapeDataString(json));
    }

    [Fact]
    public async Task An_expired_pending_cookie_is_ignored()
    {
        using var site = Site();
        var json = JsonSerializer.Serialize(new MatIdentityPendingLink("sub-expired", VictimAddress, null, null, "admin", null, VictimId.ToString()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var value = site.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(MatIdentityPendingLink.Purpose).ToTimeLimitedDataProtector()
            .Protect(json, DateTimeOffset.UtcNow.AddSeconds(-1));
        await AssertIgnoredAsync(site, value);
    }

    [Fact]
    public async Task A_user_renamed_in_between_is_not_linked()
    {
        using var site = Site();
        var (target, _, browser) = await MatIdentitySignInAsync(site);
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();
        site.Update(data => data.Users.Single(u => u.Id == VictimId).UserName = "renamed@example.com");

        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, target, html, "renamed@example.com", AttackerPassword)).StatusCode);
        Assert.Null(Victim(site).ExternalSubject);
    }

    /// <summary>The app with the attacker's user: its name is the victim's address, its password the attacker's.</summary>
    private static IdentityFactory Site()
    {
        var site = new IdentityFactory();
        site.Update(data => data.Users.Add(new AppUser
        {
            Id = VictimId,
            UserName = VictimAddress,
            PasswordHash = PersistentStore.HashPassword(AttackerPassword),
            Role = UserRole.User,
            CreateDate = DateTimeOffset.UtcNow,
            UpdateDate = DateTimeOffset.UtcNow
        }));
        return site;
    }

    private static MatIdentityPerson Person() => new("sub-victim", "Victim@Example.com", "Victim", "victim", "admin", "id-token-victim", new());

    /// <summary>The victim signs in at MatIdentity (the callback without a running MatIdentity); a browser that keeps what the callback set.</summary>
    private static async Task<(string Target, string SetCookie, HttpClient Browser)> MatIdentitySignInAsync(IdentityFactory site)
    {
        using var scope = site.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");

        var target = await MatIdentitySignIn.CompleteSignInAsync(http, Person(), "/");
        var setCookie = http.Response.Headers.SetCookie.ToString();

        var cookies = new CookieContainer();
        var pending = PendingCookieRegex().Match(setCookie);
        if (pending.Success)
        {
            cookies.Add(new Uri("http://localhost"), new Cookie(MatIdentityPendingLink.CookieName, pending.Groups[1].Value, "/"));
        }

        return (target, setCookie, site.Browser(cookies));
    }

    private static async Task AssertIgnoredAsync(IdentityFactory site, string cookieValue)
    {
        var cookies = new CookieContainer();
        cookies.Add(new Uri("http://localhost"), new Cookie(MatIdentityPendingLink.CookieName, cookieValue, "/"));
        var browser = site.Browser(cookies);

        var html = await (await browser.GetAsync("/Login?local=1&link=1")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.DoesNotContain("id=\"login-link\"", html);

        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Login?local=1&link=1", html, VictimAddress, AttackerPassword)).StatusCode);
        Assert.Null(Victim(site).ExternalSubject);
        Assert.DoesNotContain(Store(site).Read().Users, u => u.ExternalSubject is "sub-forged" or "sub-expired");
    }

    private static PersistentStore Store(IdentityFactory site) => site.Services.GetRequiredService<PersistentStore>();

    private static AppUser Victim(IdentityFactory site) => Store(site).Read().Users.Single(u => u.Id == VictimId);

    [GeneratedRegex(@"\.MatPlatform\.PendingLink=([^;]+)")]
    private static partial Regex PendingCookieRegex();
}
