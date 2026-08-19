using Microsoft.Extensions.Hosting;
using NetSplit.Core;

namespace NetSplit.Service;

public sealed class CoordinatorHostedService : BackgroundService
{
    private readonly NetSplitCoordinator _coordinator;
    private readonly FileLogBuffer _logs;

    public CoordinatorHostedService(NetSplitCoordinator coordinator, FileLogBuffer logs)
    {
        _coordinator = coordinator;
        _logs = logs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _coordinator.InitializeAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _coordinator.MaintainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                try
                {
                    await _logs.WriteAsync(
                        "ERROR",
                        $"后台维护失败：{exception.Message}",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A logging failure must not terminate the maintenance loop.
                }
            }
        }
    }
}
