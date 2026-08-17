using System.Text;
using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Instances;

public class SetupModel(PersistentStore store) : AppPageModel(store)
{
    public MatBuInstance Instance { get; private set; } = new();
    public string Compose { get; private set; } = string.Empty;
    public string? Error { get; private set; }

    public IActionResult OnGet(long id)
    {
        var result = LoadSetup(id);
        return result ?? Page();
    }

    public IActionResult OnGetDownload(long id)
    {
        var result = LoadSetup(id);
        if (result is not null) return result;
        if (string.IsNullOrWhiteSpace(Compose)) return BadRequest(Error ?? "Das Compose konnte nicht erzeugt werden.");
        return File(Encoding.UTF8.GetBytes(Compose), "application/yaml", "docker-compose.yml");
    }

    public IActionResult OnPostRegenerate(long id)
    {
        var authorization = AuthorizeEditor();
        if (authorization is not null) return authorization;
        var snapshot = Store.Read().Instances.FirstOrDefault(item => item.Id == id);
        if (snapshot is null || snapshot.Role != InstanceRole.Secondary) return NotFound();

        var token = SecondaryComposeGenerator.GenerateToken();
        Store.Update(data =>
        {
            var instance = data.Instances.First(item => item.Id == id);
            Store.SetInstanceToken(data, id, token);
            instance.Status = InstanceStatus.Unknown;
            instance.LastSeenDate = null;
            instance.LastMessage = "Instance-Token wurde erneuert; Secondary muss mit dem neuen Compose neu gestartet werden.";
            instance.UpdateDate = DateTimeOffset.UtcNow;
            instance.UpdateUserId = CurrentUser!.Id;
        });
        TempData["Message"] = "Token erneuert. Das bisherige Compose ist sofort ungültig; lade das neue Compose auf der Secondary.";
        return RedirectToPage(new { id });
    }

    private IActionResult? LoadSetup(long id)
    {
        var authorization = AuthorizeEditor();
        if (authorization is not null) return authorization;
        var instance = Store.Read().Instances.FirstOrDefault(item => item.Id == id);
        if (instance is null || instance.Role != InstanceRole.Secondary) return NotFound();
        Instance = instance;
        ViewData["Title"] = $"{instance.Name} einrichten";
        ViewData["UserName"] = CurrentUser!.UserName;

        var token = Store.GetInstanceToken(id);
        if (string.IsNullOrWhiteSpace(token))
        {
            Error = "Für diese Secondary ist kein lesbares Token gespeichert. Erzeuge unten ein neues Token.";
            return null;
        }

        try { Compose = SecondaryComposeGenerator.Generate(instance.Endpoint, token); }
        catch (ArgumentException exception) { Error = exception.Message; }
        return null;
    }

    private IActionResult? AuthorizeEditor()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        return CurrentUser!.Role == UserRole.User ? Forbid() : null;
    }
}
