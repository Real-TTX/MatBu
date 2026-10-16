using System.Security.Cryptography;
using MatBu.Data;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MatBu.Pages;

/// <summary>
/// The local sign-in. With MatIdentity configured (MATIDENTITY_AUTHORITY) a visit goes straight on to MatIdentity; the local form stays
/// reachable with <c>?local=1</c> (a way in for the administrator when MatIdentity is down), <c>?signedOut=1</c> (back from the sign-out)
/// and <c>?ssoError=1</c> (the sign-in at MatIdentity failed).
/// </summary>
[EnableRateLimiting("login")]
public class LoginModel(PersistentStore store, MatPlatformSettings platform, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty] public string UserName { get; set; } = "admin";
    [BindProperty] public string Password { get; set; } = "";
    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>True when the sign-in through MatIdentity is configured.</summary>
    public bool IdentityEnabled => platform.IdentityEnabled;

    /// <summary><c>?ssoError=1</c>: the sign-in at MatIdentity failed or MatIdentity is not reachable.</summary>
    public bool SsoError { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (store.IsSessionValid(Request.Cookies["matbu_session"])) return RedirectToPage("/Index");

        SsoError = QueryFlag("ssoError");

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
        var data = store.Read();
        var user = data.Users.FirstOrDefault(x => x.UserName.Equals(UserName, StringComparison.OrdinalIgnoreCase));
        if (user is null || !PersistentStore.VerifyPassword(Password, user.PasswordHash)) { ErrorMessage = "Benutzername oder Passwort ist falsch."; return Page(); }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        store.Update(current => current.UserSessions.Add(new Models.UserSession { Id = store.NextId(current.UserSessions.Select(x => x.Id)), Token = token, UserId = user.Id, ExpiresDate = DateTimeOffset.UtcNow.AddHours(12), CreateDate = DateTimeOffset.UtcNow, UpdateDate = DateTimeOffset.UtcNow }));
        Response.Cookies.Append("matbu_session", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = Request.IsHttps, MaxAge = TimeSpan.FromHours(12) });
        return RedirectToPage("/Index");
    }

    /// <summary>A flag in the query: <c>1</c> or <c>true</c>.</summary>
    private bool QueryFlag(string name) => Request.Query[name].ToString() is "1" or "true" or "True";
}
