using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MatBu.Pages.Settings;

public sealed class GeneralModel(PersistentStore store, GeneralSettingsStore settingsStore) : AppPageModel(store)
{
    [BindProperty] public GeneralSettings Input { get; set; } = new();
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }
    public IReadOnlyList<SelectListItem> TimeZones { get; private set; } = [];
    public string CurrentZoneLabel { get; private set; } = "";
    public string CurrentLocalTime { get; private set; } = "";

    public IActionResult OnGet()
    {
        if (!LoadAdmin()) return CurrentUser is null ? RedirectToPage("/Login") : Forbid();
        Input = settingsStore.Read();
        LoadView();
        return Page();
    }

    public IActionResult OnPostSave()
    {
        if (!LoadAdmin()) return CurrentUser is null ? RedirectToPage("/Login") : Forbid();
        if (string.IsNullOrWhiteSpace(Input.TimeZoneId) || !TryResolve(Input.TimeZoneId))
        {
            Message = "Die gewählte Zeitzone ist auf diesem System nicht bekannt.";
            MessageIsError = true;
            LoadView();
            return Page();
        }
        try { Input = settingsStore.Save(Input); }
        catch (IOException ex) { Message = $"Einstellungen konnten nicht gespeichert werden: {ex.Message}"; MessageIsError = true; LoadView(); return Page(); }
        Message = "Zeitzone gespeichert. Der Zeitplan verwendet ab sofort diese Zeitzone.";
        MessageIsError = false;
        LoadView();
        return Page();
    }

    private bool LoadAdmin()
    {
        if (!LoadUser()) return false;
        ViewData["UserName"] = CurrentUser!.UserName;
        return CurrentUser.Role == UserRole.Admin;
    }

    private void LoadView()
    {
        TimeZones = TimeZoneInfo.GetSystemTimeZones()
            .OrderBy(zone => zone.BaseUtcOffset)
            .ThenBy(zone => zone.Id, StringComparer.OrdinalIgnoreCase)
            .Select(zone => new SelectListItem($"{zone.Id} ({FormatOffset(zone.BaseUtcOffset)})", zone.Id, zone.Id == Input.TimeZoneId))
            .ToList();
        var resolved = settingsStore.ResolveTimeZone();
        CurrentZoneLabel = resolved.Id;
        CurrentLocalTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, resolved).ToString("dd.MM.yyyy HH:mm");
    }

    private static bool TryResolve(string id)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static string FormatOffset(TimeSpan offset) =>
        $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
}
