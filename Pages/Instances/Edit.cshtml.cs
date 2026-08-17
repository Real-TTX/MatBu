using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Instances;

public class EditModel(PersistentStore store) : AppPageModel(store)
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public MatBuInstance Input { get; set; } = new() { Role = InstanceRole.Secondary };
    public bool IsPrimary { get; private set; }

    public IActionResult OnGet()
    {
        var authorization = AuthorizeEditor();
        if (authorization is not null) return authorization;

        if (Id is null)
        {
            Input.Endpoint = $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
            SetPageMetadata();
            return Page();
        }

        var instance = Store.Read().Instances.FirstOrDefault(item => item.Id == Id);
        if (instance is null) return NotFound();
        Input = instance;
        IsPrimary = instance.Role == InstanceRole.Primary;
        SetPageMetadata();
        return Page();
    }

    public IActionResult OnPost()
    {
        var authorization = AuthorizeEditor();
        if (authorization is not null) return authorization;

        var existing = Id is null ? null : Store.Read().Instances.FirstOrDefault(item => item.Id == Id);
        if (Id is not null && existing is null) return NotFound();
        IsPrimary = existing?.Role == InstanceRole.Primary;
        ValidateInput();
        SetPageMetadata();
        if (!ModelState.IsValid) return Page();

        if (existing is not null)
        {
            Store.Update(data =>
            {
                var item = data.Instances.First(instance => instance.Id == existing.Id);
                item.Name = Input.Name.Trim();
                if (item.Role == InstanceRole.Secondary)
                {
                    item.Endpoint = Input.Endpoint.Trim().TrimEnd('/');
                    item.Enabled = Input.Enabled;
                }
                else
                {
                    item.Endpoint = string.Empty;
                    item.Enabled = true;
                }
                item.UpdateDate = DateTimeOffset.UtcNow;
                item.UpdateUserId = CurrentUser!.Id;
            });
            return RedirectToPage("/Instances/Index");
        }

        var token = SecondaryComposeGenerator.GenerateToken();
        long createdId = 0;
        Store.Update(data =>
        {
            var now = DateTimeOffset.UtcNow;
            createdId = Store.NextId(data.Instances.Select(item => item.Id));
            var instance = new MatBuInstance
            {
                Id = createdId,
                Name = Input.Name.Trim(),
                Role = InstanceRole.Secondary,
                Endpoint = Input.Endpoint.Trim().TrimEnd('/'),
                Enabled = Input.Enabled,
                Status = InstanceStatus.Unknown,
                CreateDate = now,
                CreateUserId = CurrentUser!.Id,
                UpdateDate = now,
                UpdateUserId = CurrentUser.Id
            };
            data.Instances.Add(instance);
            Store.SetInstanceToken(data, createdId, token);
        });
        return RedirectToPage("/Instances/Setup", new { id = createdId });
    }

    private IActionResult? AuthorizeEditor()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        return CurrentUser!.Role == UserRole.User ? Forbid() : null;
    }

    private void ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(Input.Name))
            ModelState.AddModelError("Input.Name", "Ein Name ist erforderlich.");
        if (IsPrimary) return;
        var endpoint = Input.Endpoint?.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            ModelState.AddModelError("Input.Endpoint", "Eine von der Secondary erreichbare HTTP- oder HTTPS-Adresse ist erforderlich.");
    }

    private void SetPageMetadata()
    {
        ViewData["Title"] = Id is null ? "Secondary erstellen" : IsPrimary ? "Primary bearbeiten" : "Secondary bearbeiten";
        ViewData["UserName"] = CurrentUser?.UserName;
    }
}
