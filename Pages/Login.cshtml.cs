using System.Security.Cryptography;
using MatBu.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatBu.Pages;

public class LoginModel(PersistentStore store) : PageModel
{
    [BindProperty] public string UserName { get; set; } = "admin";
    [BindProperty] public string Password { get; set; } = "";
    [TempData] public string? ErrorMessage { get; set; }

    public IActionResult OnGet() => store.IsSessionValid(Request.Cookies["matbu_session"]) ? RedirectToPage("/Index") : Page();

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
}
