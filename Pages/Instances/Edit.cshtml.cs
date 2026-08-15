using MatBu.Data;
using MatBu.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Instances;

public class EditModel(PersistentStore store) : AppPageModel(store)
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public MatBuInstance Input { get; set; } = new() { Role = InstanceRole.Secondary };
    [BindProperty] public string? InstanceToken { get; set; }
    public string? Error { get; private set; }

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role == UserRole.User) return Forbid();
        if (Id is not null) Input = Store.Read().Instances.FirstOrDefault(x => x.Id == Id) ?? new MatBuInstance();
        ViewData["UserName"] = CurrentUser.UserName;
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role == UserRole.User) return Forbid();
        if (string.IsNullOrWhiteSpace(Input.Name)) { Error = "Ein Name ist erforderlich."; return Page(); }
        if (Input.Role == InstanceRole.Secondary && !string.IsNullOrWhiteSpace(Input.Endpoint) && !Uri.TryCreate(Input.Endpoint, UriKind.Absolute, out _)) { Error = "Der Primary-Endpunkt muss eine gültige URL sein."; return Page(); }
        if (Input.Role == InstanceRole.Secondary && Id is null && string.IsNullOrWhiteSpace(InstanceToken)) { Error = "Für eine Secondary-Instanz ist ein Instance-Token erforderlich."; return Page(); }

        var now = DateTimeOffset.UtcNow;
        Store.Update(data =>
        {
            if (Id is null)
            {
                Input.Id = Store.NextId(data.Instances.Select(x => x.Id));
                Input.CreateDate = Input.UpdateDate = now;
                Input.Status = InstanceStatus.Unknown;
                data.Instances.Add(Input);
                if (!string.IsNullOrWhiteSpace(InstanceToken)) Store.SetInstanceToken(data, Input.Id, InstanceToken);
            }
            else
            {
                var item = data.Instances.First(x => x.Id == Id);
                item.Name = Input.Name; item.Role = Input.Role; item.Endpoint = Input.Endpoint; item.Enabled = Input.Enabled; item.UpdateDate = now;
                if (!string.IsNullOrWhiteSpace(InstanceToken)) Store.SetInstanceToken(data, item.Id, InstanceToken);
            }
        });
        return RedirectToPage("/Instances/Index");
    }
}
