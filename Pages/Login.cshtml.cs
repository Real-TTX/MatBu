using System.Globalization;
using MatBu.Data;
using MatBu.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MatBu.Pages;

/// <summary>
/// The local sign-in. With MatIdentity configured (MATIDENTITY_AUTHORITY) a visit goes straight on to MatIdentity; the local form stays
/// reachable with <c>?local=1</c> (a way in for the administrator when MatIdentity is down), <c>?signedOut=1</c> (back from the sign-out)
/// and <c>?ssoError=1</c> (the sign-in at MatIdentity failed). <c>?link=1</c>: MatIdentity signed in a person whose e-mail address is the name of a
/// local user that is not linked yet (a <see cref="MatIdentityPendingLink"/>); the user is linked when exactly that user signs in here with its password.
/// </summary>
[EnableRateLimiting("login")]
public class LoginModel(PersistentStore store, UserAccountService accounts, MatPlatformSettings platform, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty] public string UserName { get; set; } = "admin";
    [BindProperty] public string Password { get; set; } = "";
    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>True when the sign-in through MatIdentity is configured.</summary>
    public bool IdentityEnabled => platform.IdentityEnabled;

    /// <summary><c>?ssoError=1</c>: the sign-in at MatIdentity failed or MatIdentity is not reachable.</summary>
    public bool SsoError { get; private set; }

    /// <summary>The pending link of this browser (<c>?link=1</c>), or null.</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (store.IsSessionValid(Request.Cookies[UserAccountService.SessionCookie])) return RedirectToPage("/Index");

        SsoError = QueryFlag("ssoError");
        PendingLink = MatIdentityPendingLink.Peek(HttpContext);
        if (PendingLink is not null && !string.IsNullOrWhiteSpace(PendingLink.Email)) UserName = PendingLink.Email;

        // Auto login: straight on to MatIdentity, no local form in the way.
        if (IdentityEnabled && !QueryFlag("local") && !QueryFlag("signedOut") && !SsoError)
        {
            try
            {
                await HttpContext.ChallengeAsync(MatIdentitySignIn.Scheme, new AuthenticationProperties { RedirectUri = "/" });
                return new EmptyResult();
            }
            catch (InvalidOperationException exception)
            {
                // MatIdentity cannot be reached (its configuration cannot be read): the local form is the way in.
                logger.LogWarning(exception, "MatIdentity is not reachable, showing the local sign-in");
                SsoError = true;
            }
        }

        return Page();
    }

    public IActionResult OnPost()
    {
        var user = accounts.FindByPassword(UserName, Password);
        if (user is null)
        {
            ErrorMessage = "Benutzername oder Passwort ist falsch.";
            PendingLink = MatIdentityPendingLink.Peek(HttpContext);
            return Page();
        }

        // The password proves the account: a pending link of MatIdentity is completed for exactly the user that matched, and dropped for any other.
        string? idToken = null;
        if (MatIdentityPendingLink.TakeFor(HttpContext, user.Id.ToString(CultureInfo.InvariantCulture)) is { } link)
        {
            var linked = accounts.LinkExternal(user.Id, new ExternalIdentity(link.Subject, link.Email, link.Name, link.PreferredUserName, link.Role));
            if (linked.Succeeded) idToken = link.IdToken;
        }

        var token = accounts.CreateSession(user.Id, idToken);
        Response.Cookies.Append(UserAccountService.SessionCookie, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = Request.IsHttps, MaxAge = UserAccountService.SessionLifetime });
        return RedirectToPage("/Index");
    }

    /// <summary>Cancels a pending link of MatIdentity: nothing is linked, the local form stays.</summary>
    public IActionResult OnPostCancelLink()
    {
        MatIdentityPendingLink.Clear(HttpContext);
        return Redirect("/Login?local=1");
    }

    /// <summary>A flag in the query: <c>1</c> or <c>true</c>.</summary>
    private bool QueryFlag(string name) => Request.Query[name].ToString() is "1" or "true" or "True";
}
