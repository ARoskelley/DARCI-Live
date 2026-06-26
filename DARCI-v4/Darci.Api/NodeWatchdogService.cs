using Darci.Nodes;

namespace Darci.Api;

/// <summary>
/// Periodically reaps packets whose working lease has expired. Combined with the startup orphan
/// sweep, this guarantees no packet can stay stuck in a non-terminal state once its owner stops
/// renewing the lease — the structural fix for the task-orphaning bug.
/// </summary>
public sealed class NodeWatchdogService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly NodeWatchdog _watchdog;
    private readonly ILogger<NodeWatchdogService> _logger;

    public NodeWatchdogService(NodeWatchdog watchdog, ILogger<NodeWatchdogService> logger)
    {
        _watchdog = watchdog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _watchdog.SweepExpiredLeasesAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Node watchdog sweep failed (will retry next interval).");
            }
        }
    }
}
