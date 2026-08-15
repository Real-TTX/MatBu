using MatBu.Data;
using MatBu.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Instances;

public class IndexModel(PersistentStore store) : AppPageModel(store)
{
    public IReadOnlyList<MatBuInstance> Items { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string RoleFilter { get; set; } = "Alle";
    [BindProperty(SupportsGet = true)] public string StatusFilter { get; set; } = "Alle";
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "name";
    public bool CanEdit => CurrentUser?.Role != UserRole.User;

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        var query = Store.Read().Instances.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(Search))
            query = query.Where(x => x.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || x.Endpoint.Contains(Search, StringComparison.OrdinalIgnoreCase));
        if (RoleFilter != "Alle") query = query.Where(x => x.Role.ToString() == RoleFilter);
        if (StatusFilter != "Alle") query = query.Where(x => x.Status.ToString() == StatusFilter);
        query = Sort == "updated" ? query.OrderByDescending(x => x.UpdateDate) : query.OrderBy(x => x.Name);
        Items = query.ToList();
        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }

    public IActionResult OnPostDelete(long id)
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (!CanEdit) return Forbid();
        var data = Store.Read();
        if (id == 1 || data.Objects.Any(x => x.InstanceId == id)) return RedirectToPage("/Instances/Index");
        Store.Update(current => current.Instances.RemoveAll(x => x.Id == id));
        return RedirectToPage();
    }
}
