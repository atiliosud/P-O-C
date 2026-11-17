using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaseAgents.Infra.Messaging;

/// <summary>
/// Runs a delivery pass every <see cref="BusOptions.PumpInterval"/>, and right away after a publish.
/// A failing pass is logged and the loop continues.
/// </summary>
public sealed class EventBusPump(InMemoryEventBus bus, BusOptions options, ILogger<EventBusPump> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await bus.DeliverPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Event bus delivery pass failed; retrying on the next pass.");
            }

            try
            {
                await bus.WorkSignal.WaitAsync(options.PumpInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
