using MatBu.Data;
using MatBu.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Tasks.Labels;

public class IndexModel(PersistentStore store) : AppPageModel(store)
{
    public IReadOnlyList<JobLabel> Items { get; private set; } = [];
    public IReadOnlyDictionary<long, int> JobCounts { get; private set; } = new Dictionary<long, int>();
    public bool CanEdit => CurrentUser?.Role != UserRole.User;

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        var data = Store.Read();
        Items = data.JobLabels.OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase).ToList();
        JobCounts = data.BackupTaskLabels.GroupBy(item => item.JobLabelId).ToDictionary(group => group.Key, group => group.Count());
        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }

    public IActionResult OnPostDelete(long id)
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        if (!CanEdit) return Forbid();
        var data = Store.Read();
        var label = data.JobLabels.FirstOrDefault(item => item.Id == id);
        if (label is null) return NotFound();
        if (data.BackupTaskLabels.Any(item => item.JobLabelId == id))
        {
            TempData["LabelError"] = "Der Tag ist noch Jobs zugewiesen und kann deshalb nicht gelöscht werden.";
            return RedirectToPage();
        }
        Store.Update(current => current.JobLabels.RemoveAll(item => item.Id == id));
        TempData["LabelSuccess"] = $"Tag „{label.Name}“ wurde gelöscht.";
        return RedirectToPage();
    }
}
