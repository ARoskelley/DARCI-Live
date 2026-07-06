using Darci.Research.Agents;

namespace Darci.Api;

/// <summary>
/// Periodically ticks the <see cref="CampaignEligibilitySweep"/> so eligible innovated entries get a
/// validation campaign auto-DRAFTED (at low priority; still parked for human authorization). Modelled on
/// <see cref="NodeWatchdogService"/> — a standalone hosted service, deliberately NOT invoked from the
/// innovation node, so it does not recreate the node→coordinator→nodes DI cycle. No-op unless enabled.
/// </summary>
public sealed class CampaignEligibilitySweepService : BackgroundService
{
    private readonly CampaignEligibilitySweep _sweep;
    private readonly CampaignSweepOptions _options;
    private readonly ILogger<CampaignEligibilitySweepService> _logger;

    public CampaignEligibilitySweepService(
        CampaignEligibilitySweep sweep,
        CampaignSweepOptions options,
        ILogger<CampaignEligibilitySweepService> logger)
    {
        _sweep = sweep;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Campaign eligibility sweep is disabled; auto-drafting off.");
            return;
        }

        using var timer = new PeriodicTimer(_options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _sweep.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Campaign eligibility sweep failed (will retry next interval).");
            }
        }
    }
}
