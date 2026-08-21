using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Settings;

public sealed class TransferModel(PersistentStore store, TransferSettingsStore settingsStore) : AppPageModel(store)
{
    [BindProperty] public TransferSettings Input { get; set; } = new();
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }

    public IActionResult OnGet()
    {
        if (!LoadAdmin()) return CurrentUser is null ? RedirectToPage("/Login") : Forbid();
        Input = settingsStore.Read();
        return Page();
    }

    public IActionResult OnPostSave()
    {
        if (!LoadAdmin()) return CurrentUser is null ? RedirectToPage("/Login") : Forbid();
        var error = Validate(Input);
        if (error is not null) { Message = error; MessageIsError = true; return Page(); }
        try { Input = settingsStore.Save(Input); }
        catch (IOException ex) { Message = $"Einstellungen konnten nicht gespeichert werden: {ex.Message}"; MessageIsError = true; return Page(); }
        Message = "Transfer-Einstellungen gespeichert. Neue Transfers und Prüfungen verwenden ab sofort diese Werte.";
        MessageIsError = false;
        return Page();
    }

    public IActionResult OnPostReset()
    {
        if (!LoadAdmin()) return CurrentUser is null ? RedirectToPage("/Login") : Forbid();
        try { Input = settingsStore.Save(TransferSettings.FromEnvironmentDefaults()); }
        catch (IOException ex) { Message = $"Einstellungen konnten nicht gespeichert werden: {ex.Message}"; MessageIsError = true; return Page(); }
        Message = "Auf Standardwerte zurückgesetzt.";
        MessageIsError = false;
        return Page();
    }

    private bool LoadAdmin()
    {
        if (!LoadUser()) return false;
        ViewData["UserName"] = CurrentUser!.UserName;
        return CurrentUser.Role == UserRole.Admin;
    }

    private static string? Validate(TransferSettings s)
    {
        if (s.BacklogLowMiB < 8) return "Der niedrige Backlog-Schwellwert muss mindestens 8 MiB betragen.";
        if (s.BacklogHighMiB <= s.BacklogLowMiB) return "Der hohe Backlog-Schwellwert muss größer als der niedrige sein.";
        if (s.MinFreeSpaceGiB < 0) return "Die Mindest-Freispeicher-Reserve darf nicht negativ sein.";
        if (s.CacheRetentionHours is < 1 or > 8760) return "Die Cache-Aufbewahrung muss zwischen 1 und 8760 Stunden liegen.";
        if (s.SecondaryIdleTimeoutSeconds is < 5 or > 3600) return "Der Idle-Timeout muss zwischen 5 und 3600 Sekunden liegen.";
        if (s.SecondaryHeartbeatSeconds is < 2 or > 60) return "Das Heartbeat-Intervall muss zwischen 2 und 60 Sekunden liegen.";
        if (s.SecondaryBuildStallSeconds is < 120 or > 21600) return "Das Build-Stall-Fenster muss zwischen 120 und 21600 Sekunden liegen.";
        return null;
    }
}
