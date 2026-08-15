using MatBu.Data;
using MatBu.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages;

public class UsersModel(PersistentStore store) : AppPageModel(store)
{
    public IReadOnlyList<AppUser> Items { get; private set; } = [];
    public IReadOnlySet<long> ActiveSessionUserIds { get; private set; } = new HashSet<long>();
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string RoleFilter { get; set; } = "Alle";
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "name";

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role != UserRole.Admin) return Forbid();

        var data = Store.Read();
        ActiveSessionUserIds = data.UserSessions
            .Where(x => x.ExpiresDate > DateTimeOffset.UtcNow)
            .Select(x => x.UserId)
            .ToHashSet();
        var query = data.Users.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(Search)) query = query.Where(x => x.UserName.Contains(Search, StringComparison.OrdinalIgnoreCase));
        if (RoleFilter != "Alle") query = query.Where(x => x.Role.ToString() == RoleFilter);
        query = Sort == "updated" ? query.OrderByDescending(x => x.UpdateDate) : query.OrderBy(x => x.UserName);
        Items = query.ToList();
        ViewData["UserName"] = CurrentUser.UserName;
        return Page();
    }

    public IActionResult OnPostDelete(long id)
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role != UserRole.Admin) return Forbid();
        Store.Update(data =>
        {
            if (data.Users.Count(x => x.Role == UserRole.Admin) > 1 || data.Users.FirstOrDefault(x => x.Id == id)?.Role != UserRole.Admin)
            {
                data.Users.RemoveAll(x => x.Id == id);
                data.UserSessions.RemoveAll(x => x.UserId == id);
            }
        });
        return RedirectToPage();
    }
}
