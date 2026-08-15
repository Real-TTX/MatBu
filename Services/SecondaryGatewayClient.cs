using System.Text.Json;
using MatBu.Data;
using MatBu.Models;

namespace MatBu.Services;

public sealed record GatewayObjectTestRequest(ObjectKind Kind, ObjectDirection Direction, string Location, string? SmbUsername, string? SmbPassword);
public sealed record GatewayObjectTestResult(bool Success, string Message, long DurationMs);

public sealed class SecondaryGatewayClient(
    PersistentStore store,
    SecondaryCommandService commands,
    ILogger<SecondaryGatewayClient> logger)
{
    public async Task<GatewayObjectTestResult> TestObjectAsync(MatBuInstance instance, BackupObject item, (string Username, string Password)? credential, CancellationToken cancellationToken)
    {
        if (instance.Role != InstanceRole.Secondary) return await FailAsync(instance.Id, "Das Object ist keiner Secondary-Instanz zugeordnet.");
        if (string.IsNullOrWhiteSpace(store.GetInstanceToken(instance.Id))) return await FailAsync(instance.Id, "Für die Secondary-Instanz ist kein Instance-Token hinterlegt.");

        try
        {
            var request = new GatewayObjectTestRequest(item.Kind, item.Direction, item.Location, credential?.Username, credential?.Password);
            var commandId = commands.Queue(instance.Id, SecondaryCommandKind.ObjectTest, Guid.NewGuid().ToString("N"), request);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var command = await commands.WaitForCompletionAsync(commandId, timeout.Token);
            if (command.State != "Completed") return await FailAsync(instance.Id, string.IsNullOrWhiteSpace(command.Error) ? "Secondary konnte den Verbindungstest nicht ausführen." : command.Error);
            var result = JsonSerializer.Deserialize<GatewayObjectTestResult>(command.ResultJson);
            if (result is null) return await FailAsync(instance.Id, "Secondary antwortete ohne Testergebnis.");
            UpdateInstance(instance.Id, result.Success ? InstanceStatus.Online : InstanceStatus.Online, result.Message);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(instance.Id, "Die Secondary hat den Verbindungstest nicht rechtzeitig abgeholt.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reverse object test failed on instance {InstanceId}", instance.Id);
            return await FailAsync(instance.Id, $"Secondary nicht erreichbar: {ex.Message}");
        }
    }

    private Task<GatewayObjectTestResult> FailAsync(long instanceId, string message)
    {
        UpdateInstance(instanceId, InstanceStatus.Offline, message);
        return Task.FromResult(new GatewayObjectTestResult(false, message, 0));
    }

    private void UpdateInstance(long instanceId, InstanceStatus status, string message)
    {
        store.Update(data =>
        {
            var instance = data.Instances.FirstOrDefault(x => x.Id == instanceId);
            if (instance is not null)
            {
                instance.Status = status;
                instance.LastSeenDate = DateTimeOffset.UtcNow;
                instance.LastMessage = message;
                instance.UpdateDate = DateTimeOffset.UtcNow;
            }
        });
    }
}
