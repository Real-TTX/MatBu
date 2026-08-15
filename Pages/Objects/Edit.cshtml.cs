using MatBu.Data;
using MatBu.Models;
using MatBu.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBu.Pages.Objects;

public class EditModel(
    PersistentStore store,
    SecondaryGatewayClient gateway,
    ObjectConnectivityTester connectivityTester) : AppPageModel(store)
{
    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public BackupObject Input { get; set; } = new();

    [BindProperty]
    public string? SmbUsername { get; set; }

    [BindProperty]
    public string? SmbPassword { get; set; }

    public IReadOnlyList<MatBuInstance> Instances { get; private set; } = [];
    public bool HasStoredSmbCredential { get; private set; }
    public string? SmbPathSummary { get; private set; }

    public IActionResult OnGet()
    {
        var authorization = AuthorizeAdmin();
        if (authorization is not null) return authorization;

        var data = Store.Read();
        Instances = data.Instances;
        if (Id is not null)
        {
            Input = data.Objects.FirstOrDefault(item => item.Id == Id) ?? new BackupObject();
            var credential = Store.GetSmbCredential(Id.Value);
            SmbUsername = credential?.Username;
            HasStoredSmbCredential = credential is not null;
        }

        SetPageMetadata();
        UpdateSmbPathSummary();
        return Page();
    }

    public IActionResult OnPost()
    {
        var authorization = AuthorizeAdmin();
        if (authorization is not null) return authorization;

        PreparePostedPage();
        if (!ValidateInput()) return Page();

        SaveInput();
        return RedirectToPage("/Objects/Index");
    }

    public async Task<IActionResult> OnPostTest(CancellationToken cancellationToken)
    {
        var authorization = AuthorizeAdmin();
        if (authorization is not null) return authorization;

        PreparePostedPage();
        if (!ValidateInput()) return Page();

        var objectId = SaveInput();
        var data = Store.Read();
        var item = data.Objects.First(objectItem => objectItem.Id == objectId);
        var instance = data.Instances.FirstOrDefault(current => current.Id == item.InstanceId);
        var credential = Store.GetSmbCredential(objectId);

        GatewayObjectTestResult result;
        if (instance is null)
        {
            result = new GatewayObjectTestResult(false, "Die zugeordnete MatBu-Instanz wurde nicht gefunden.", 0);
        }
        else if (instance.Role == InstanceRole.Secondary)
        {
            result = await gateway.TestObjectAsync(instance, item, credential, cancellationToken);
        }
        else
        {
            result = await connectivityTester.TestAsync(item, credential?.Username, credential?.Password, cancellationToken);
        }

        Store.Update(currentData =>
        {
            var current = currentData.Objects.FirstOrDefault(objectItem => objectItem.Id == objectId);
            if (current is null) return;
            current.Status = result.Success ? ObjectStatus.Healthy : ObjectStatus.Warning;
            current.LastTestDate = DateTimeOffset.UtcNow;
            current.LastTestMessage = result.Message;
            current.UpdateDate = DateTimeOffset.UtcNow;
            current.UpdateUserId = CurrentUser!.Id;
        });

        TempData["Message"] = $"{result.Message} ({result.DurationMs} ms)";
        return RedirectToPage("/Objects/Edit", new { id = objectId });
    }

    private IActionResult? AuthorizeAdmin()
    {
        if (!LoadUser()) return RedirectToPage("/Login");
        return CurrentUser!.Role == UserRole.User ? Forbid() : null;
    }

    private void PreparePostedPage()
    {
        Input.Detail ??= string.Empty;
        ModelState.Remove("Input.Detail");
        Instances = Store.Read().Instances;
        HasStoredSmbCredential = Id is not null && Store.GetSmbCredential(Id.Value) is not null;
        SetPageMetadata();
        UpdateSmbPathSummary();
    }

    private bool ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(Input.Name))
            ModelState.AddModelError("Input.Name", "Der Name ist erforderlich.");
        if (string.IsNullOrWhiteSpace(Input.Location))
            ModelState.AddModelError("Input.Location", "Die Adresse ist erforderlich.");
        if (!Instances.Any(instance => instance.Id == Input.InstanceId))
            ModelState.AddModelError("Input.InstanceId", "Die ausgewählte Instanz existiert nicht.");

        if (Input.Kind != ObjectKind.Smb)
            return ModelState.IsValid;

        if (!SmbPath.TryParse(Input.Location, out var smbLocation, out var pathError))
        {
            ModelState.AddModelError("Input.Location", pathError ?? "Der SMB-Pfad ist ungültig.");
            return false;
        }

        Input.Location = smbLocation!.UncPath;
        SmbPathSummary = smbLocation.Summary;

        if (string.IsNullOrWhiteSpace(SmbUsername))
            return ModelState.IsValid;

        var storedCredential = Id is null ? null : Store.GetSmbCredential(Id.Value);
        var reusesStoredPassword = storedCredential is not null &&
            string.Equals(storedCredential.Value.Username, SmbUsername.Trim(), StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(SmbPassword) && !reusesStoredPassword)
            ModelState.AddModelError(nameof(SmbPassword), "Für einen neuen SMB-Benutzer muss ein Passwort eingegeben werden.");

        return ModelState.IsValid;
    }

    private long SaveInput()
    {
        var now = DateTimeOffset.UtcNow;
        var userId = CurrentUser!.Id;
        var storedCredential = Id is null ? null : Store.GetSmbCredential(Id.Value);
        var normalizedUsername = SmbUsername?.Trim();
        var passwordToStore = !string.IsNullOrEmpty(SmbPassword)
            ? SmbPassword
            : storedCredential is not null && string.Equals(storedCredential.Value.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase)
                ? storedCredential.Value.Password
                : null;
        var objectId = Id;

        Store.Update(data =>
        {
            if (objectId is null)
            {
                objectId = Store.NextId(data.Objects.Select(item => item.Id));
                Input.Id = objectId.Value;
                Input.Status = ObjectStatus.Warning;
                Input.CreateDate = now;
                Input.CreateUserId = userId;
                Input.UpdateDate = now;
                Input.UpdateUserId = userId;
                data.Objects.Add(Input);
            }
            else
            {
                var item = data.Objects.First(current => current.Id == objectId.Value);
                item.Name = Input.Name.Trim();
                item.Kind = Input.Kind;
                item.Direction = Input.Direction;
                item.Location = Input.Location.Trim();
                item.Detail = Input.Detail?.Trim() ?? string.Empty;
                item.InstanceId = Input.InstanceId;
                item.UpdateDate = now;
                item.UpdateUserId = userId;
            }

            data.SmbCredentials.RemoveAll(credential => credential.ObjectId == objectId.Value &&
                (Input.Kind != ObjectKind.Smb || string.IsNullOrWhiteSpace(normalizedUsername)));
            if (Input.Kind == ObjectKind.Smb && !string.IsNullOrWhiteSpace(normalizedUsername) && passwordToStore is not null)
                Store.SetSmbCredential(data, objectId.Value, normalizedUsername, passwordToStore);
        });

        Id = objectId;
        return objectId!.Value;
    }

    private void UpdateSmbPathSummary()
    {
        SmbPathSummary = Input.Kind == ObjectKind.Smb && SmbPath.TryParse(Input.Location, out var location, out _)
            ? location!.Summary
            : null;
    }

    private void SetPageMetadata() => ViewData["UserName"] = CurrentUser?.UserName;
}
