using MatBu.Data;
using MatBu.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Settings;

public class MonitoringModel(PersistentStore store) : AppPageModel(store)
{
    public string Token { get; private set; } = "";
    public string? Message { get; private set; }
    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role != UserRole.Admin) return Forbid();
        Token = Store.GetMonitoringToken(); ViewData["UserName"] = CurrentUser.UserName; return Page();
    }
    public IActionResult OnPostRegenerate()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role != UserRole.Admin) return Forbid();
        Token = Store.RegenerateMonitoringToken(); Message = "Das Monitoring-Token wurde neu erzeugt. Alte Aufrufe sind sofort ungültig."; ViewData["UserName"] = CurrentUser.UserName; return Page();
    }
}
