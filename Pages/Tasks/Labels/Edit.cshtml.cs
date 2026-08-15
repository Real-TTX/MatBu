using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Tasks.Labels;

public class EditModel(PersistentStore store) : AppPageModel(store)
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string Color { get; set; } = "#0b7f8a";

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role == UserRole.User) return Forbid();
        if (Id is not null)
        {
            var item = Store.Read().JobLabels.FirstOrDefault(label => label.Id == Id);
            if (item is null) return NotFound();
            Name = item.Name;
            Color = item.Color;
        }
        ViewData["UserName"] = CurrentUser.UserName;
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (CurrentUser!.Role == UserRole.User) return Forbid();
        var data = Store.Read();
        Name = Name.Trim();
        Color = JobLabelSnapshots.NormalizeColor(Color);
        if (Name.Length is < 1 or > 80) ModelState.AddModelError(nameof(Name), "Der Tag-Name muss zwischen 1 und 80 Zeichen lang sein.");
        if (Name.IndexOfAny(['\r', '\n']) >= 0) ModelState.AddModelError(nameof(Name), "Der Tag-Name darf keine Zeilenumbrüche enthalten.");
        if (data.JobLabels.Any(label => label.Id != Id && label.Name.Equals(Name, StringComparison.OrdinalIgnoreCase)))
            ModelState.AddModelError(nameof(Name), "Ein Tag mit diesem Namen existiert bereits.");
        if (!ModelState.IsValid) return Page();
        Store.Update(current =>
        {
            var now = DateTimeOffset.UtcNow;
            if (Id is null)
            {
                current.JobLabels.Add(new JobLabel
                {
                    Id = Store.NextId(current.JobLabels.Select(label => label.Id)),
                    Name = Name,
                    Color = Color,
                    CreateDate = now,
                    CreateUserId = CurrentUser.Id,
                    UpdateDate = now,
                    UpdateUserId = CurrentUser.Id
                });
                return;
            }
            var item = current.JobLabels.First(label => label.Id == Id);
            item.Name = Name;
            item.Color = Color;
            item.UpdateDate = now;
            item.UpdateUserId = CurrentUser.Id;
        });
        return RedirectToPage("/Tasks/Labels/Index");
    }
}
