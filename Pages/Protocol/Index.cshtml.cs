using MatBu.Data;
using MatBu.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Protocol;

public sealed record ProtocolListItem(JobStep Step, TransferJob? Execution, string JobName);

public class IndexModel(PersistentStore store) : AppPageModel(store)
{
    public IReadOnlyList<ProtocolListItem> Items { get; private set; } = [];
    public IReadOnlyList<string> Stages { get; private set; } = [];
    public IReadOnlyList<string> States { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string StageFilter { get; set; } = "Alle";
    [BindProperty(SupportsGet = true)] public string StateFilter { get; set; } = "Alle";
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "newest";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public int TotalPages { get; private set; }

    public IActionResult OnGet()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        var data = Store.Read();
        var executions = data.TransferJobs.ToDictionary(item => item.Id);
        Stages = data.JobSteps.Select(item => item.Stage).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToList();
        States = data.JobSteps.Select(item => item.State).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToList();

        var query = data.JobSteps.Select(step =>
        {
            executions.TryGetValue(step.TransferJobId, out var execution);
            var name = !string.IsNullOrWhiteSpace(execution?.TaskName) ? execution.TaskName : $"Job #{execution?.TaskId ?? 0}";
            return new ProtocolListItem(step, execution, name);
        });

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(item =>
                item.JobName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Step.Stage.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Step.State.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Step.Message.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Step.InstanceName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Step.Location.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        if (StageFilter != "Alle") query = query.Where(item => item.Step.Stage.Equals(StageFilter, StringComparison.OrdinalIgnoreCase));
        if (StateFilter != "Alle") query = query.Where(item => item.Step.State.Equals(StateFilter, StringComparison.OrdinalIgnoreCase));
        query = Sort == "oldest" ? query.OrderBy(item => item.Step.CreateDate) : query.OrderByDescending(item => item.Step.CreateDate);

        var all = query.ToList();
        TotalPages = Math.Max(1, (int)Math.Ceiling(all.Count / 25d));
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        Items = all.Skip((PageNumber - 1) * 25).Take(25).ToList();
        ViewData["UserName"] = CurrentUser!.UserName;
        return Page();
    }
}
