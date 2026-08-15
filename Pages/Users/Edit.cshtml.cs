using MatBu.Data;
using MatBu.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Users;

public class EditModel(PersistentStore store) : AppPageModel(store)
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string UserName { get; set; } = "";
    [BindProperty] public UserRole Role { get; set; } = UserRole.User;
    [BindProperty] public string? Password { get; set; }
    public string? Error { get; private set; }
    public IActionResult OnGet() { if (!LoadUser()) return RedirectToPage("/Login"); if (CurrentUser!.Role != UserRole.Admin) return Forbid(); var item = Id is null ? null : Store.Read().Users.FirstOrDefault(x => x.Id == Id); if (item is not null) { UserName = item.UserName; Role = item.Role; } ViewData["UserName"] = CurrentUser.UserName; return Page(); }
    public IActionResult OnPost()
    {
        if (!LoadUser()) return RedirectToPage("/Login"); if (CurrentUser!.Role != UserRole.Admin) return Forbid();
        if (string.IsNullOrWhiteSpace(UserName)) { Error = "Ein Benutzername ist erforderlich."; return Page(); }
        var duplicate = Store.Read().Users.Any(x => x.UserName.Equals(UserName, StringComparison.OrdinalIgnoreCase) && x.Id != Id); if (duplicate) { Error = "Der Benutzername ist bereits vergeben."; return Page(); }
        if (Id is null && string.IsNullOrWhiteSpace(Password)) { Error = "Für neue Benutzer ist ein Passwort erforderlich."; return Page(); }
        Store.Update(data => { var now = DateTimeOffset.UtcNow; if (Id is null) data.Users.Add(new AppUser { Id = Store.NextId(data.Users.Select(x => x.Id)), UserName = UserName.Trim(), Role = Role, PasswordHash = PersistentStore.HashPassword(Password!), CreateDate = now, UpdateDate = now }); else { var item = data.Users.First(x => x.Id == Id); item.UserName = UserName.Trim(); item.Role = Role; if (!string.IsNullOrWhiteSpace(Password)) item.PasswordHash = PersistentStore.HashPassword(Password); item.UpdateDate = now; } });
        return RedirectToPage("/Users");
    }
}
