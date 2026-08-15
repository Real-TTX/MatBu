using MatBu.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatBu.Pages;

public class LogoutModel(PersistentStore store) : PageModel
{
    public IActionResult OnPost()
    {
        var token = Request.Cookies["matbu_session"];
        store.Update(data => data.UserSessions.RemoveAll(x => x.Token == token));
        Response.Cookies.Delete("matbu_session");
        return RedirectToPage("/Login");
    }
}
