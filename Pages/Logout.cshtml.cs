using MatBu.Services;
using MatPlatform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatBu.Pages;

/// <summary>Ends the session; after a sign-in through MatIdentity the session there ends too.</summary>
public class LogoutModel(UserAccountService accounts, MatPlatformSettings platform) : PageModel
{
    public IActionResult OnPost()
    {
        var token = Request.Cookies[UserAccountService.SessionCookie];
        // Read before the session is gone: only a sign-in through MatIdentity carries an ID token.
        var identityToken = platform.IdentityEnabled ? accounts.GetIdToken(token) : null;
        accounts.EndSession(token);
        Response.Cookies.Delete(UserAccountService.SessionCookie);

        if (identityToken is not null)
        {
            // Then at MatIdentity as well; it sends the browser back to /signout-callback-oidc and on to /Login?signedOut=1.
            return MatIdentitySignIn.SignOutAtIdentity(HttpContext, identityToken);
        }

        // With MatIdentity the sign-in page would go straight back to it; ?signedOut=1 stops there.
        return platform.IdentityEnabled ? Redirect("/Login?signedOut=1") : RedirectToPage("/Login");
    }
}
