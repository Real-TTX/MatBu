namespace MatBu.Services;

public sealed class ConsistencyRecoveryWorker(DockerConsistencyService consistency, ILogger<ConsistencyRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var isSecondary = string.Equals(Environment.GetEnvironmentVariable("MATBU_INSTANCE_ROLE"), "Secondary", StringComparison.OrdinalIgnoreCase);
        var isTransferWorker = string.Equals(Environment.GetEnvironmentVariable("MATBU_DOCKER_WORKER"), "true", StringComparison.OrdinalIgnoreCase);
        if (!isSecondary && !isTransferWorker) return;
        try
        {
            await consistency.RecoverPendingAsync(stoppingToken);
            logger.LogInformation("Docker consistency recovery check completed");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogError(exception, "Docker consistency recovery check failed"); }
    }
}
