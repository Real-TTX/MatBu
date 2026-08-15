using MatBu.Data;
using MatBu.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Objects;

public class IndexModel(PersistentStore store) : AppPageModel(store)
{
    public IReadOnlyList<BackupObject> Items { get; private set; } = [];
    public IReadOnlyDictionary<long, string> InstanceNames { get; private set; } = new Dictionary<long, string>();
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? KindFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "name";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public int TotalPages { get; private set; }
    public bool CanEdit => CurrentUser?.Role != UserRole.User;

    public string GetInstanceLabel(long instanceId) => InstanceNames.TryGetValue(instanceId, out var label) ? label : "Unbekannt";

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");

        var data = Store.Read();
        InstanceNames = data.Instances.ToDictionary(x => x.Id, x => $"{x.Name} ({x.Role})");
        var query = data.Objects.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(Search)) query = query.Where(x => x.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || x.Location.Contains(Search, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(KindFilter) && KindFilter != "Alle") query = query.Where(x => x.Kind.ToString().Equals(KindFilter, StringComparison.OrdinalIgnoreCase));
        query = Sort == "updated" ? query.OrderByDescending(x => x.UpdateDate) : query.OrderBy(x => x.Name);
        var all = query.ToList();
        TotalPages = Math.Max(1, (int)Math.Ceiling(all.Count / 10d));
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        Items = all.Skip((PageNumber - 1) * 10).Take(10).ToList();
        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }

    public IActionResult OnPostDelete(long id)
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (!CanEdit) return Forbid();
        Store.Update(data => { data.Objects.RemoveAll(x => x.Id == id); data.SmbCredentials.RemoveAll(x => x.ObjectId == id); });
        return RedirectToPage();
    }
}
