// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// The exercise heartbeat: every two seconds, advance every
/// <see cref="IScenarioTick"/> in the host and let it broadcast.
/// </summary>
/// <remarks>
/// <para>
/// One loop rather than one per service. Each service used to own a
/// <c>Task.Run</c> mover plus the <see cref="IDisposable"/> needed to
/// stop it again; with three services that is the same twenty lines
/// written out three times, three shutdown paths to get right, and three
/// tick phases drifting apart so a vehicle and the drone mounted on it
/// report positions from different instants.
/// </para>
/// <para>
/// A hosted service also gets shutdown for free: ASP.NET Core cancels
/// <see cref="BackgroundService.ExecuteAsync"/> on host stop, so nothing
/// here has to be disposed by hand.
/// </para>
/// </remarks>
internal sealed class ScenarioTicker(
    IEnumerable<IScenarioTick> parts,
    ExerciseClock clock) : BackgroundService
{
    /// <summary>
    /// Broadcast cadence. Slow enough to read in the workbench's frame
    /// pane, fast enough that a track visibly moves between frames.
    /// </summary>
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var elapsedSeconds = clock.ElapsedSeconds;
                foreach (var part in parts)
                {
                    part.Tick(elapsedSeconds);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown — the only way out of the loop.
        }
    }
}
