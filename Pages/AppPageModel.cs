using MatBu.Data;
using MatBu.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatBu.Pages;

public abstract class AppPageModel(PersistentStore store) : PageModel
{
    protected PersistentStore Store { get; } = store;
    public AppUser? CurrentUser { get; private set; }

    protected bool LoadUser()
    {
        var data = Store.Read();
        var token = Request.Cookies["matbu_session"];
        var session = data.UserSessions.FirstOrDefault(x => x.Token == token && x.ExpiresDate > DateTimeOffset.UtcNow);
        CurrentUser = session is null ? null : data.Users.FirstOrDefault(x => x.Id == session.UserId);
        return CurrentUser is not null;
    }
}
