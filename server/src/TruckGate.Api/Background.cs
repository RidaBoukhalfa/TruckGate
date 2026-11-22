using TruckGate.Application;
using TruckGate.Application.Alerts;

namespace TruckGate.Api;

/// <summary>Runs the watchdog every minute (undecided entries, missing exits, phones not syncing, daily sample).</summary>
internal sealed class WatchdogHost(Watchdog watchdog, ILogger<WatchdogHost> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                var r = await watchdog.RunOnceAsync(stoppingToken);
                if (r.UndecidedEntries + r.NoExit + r.SyncOverdue + r.SampleTrips > 0)
                    log.LogInformation("Watchdog: {Undecided} undecided, {NoExit} without exit, {Overdue} phones overdue, {Sample} sampled",
                        r.UndecidedEntries, r.NoExit, r.SyncOverdue, r.SampleTrips);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Watchdog run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Delivers alerts by SMS and Telegram every 15 seconds.</summary>
internal sealed class AlertDispatchHost(AlertService alerts, ILogger<AlertDispatchHost> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var r = await alerts.DispatchOnceAsync(stoppingToken);
                if (r.Failed > 0) log.LogWarning("Alerts: {Sent} sent, {Failed} failed, {Skipped} held back", r.Sent, r.Failed, r.Skipped);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Alert dispatch failed");
            }
        }
    }
}
