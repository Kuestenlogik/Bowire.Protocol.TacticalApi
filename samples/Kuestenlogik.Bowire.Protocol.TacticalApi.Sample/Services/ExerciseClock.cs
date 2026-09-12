// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// How long the exercise has been running. One clock for the whole
/// sample, injected everywhere a position is evaluated.
/// </summary>
/// <remarks>
/// When the sample served one service it kept its start time as a field
/// on that service. Three services would have meant three start times,
/// set milliseconds apart at container build — and a blue force riding a
/// vehicle would then have been reported a metre or two away from the
/// vehicle it is bolted to, for no reason a reader could see. The clock
/// is shared so "the same instant" means the same instant.
/// </remarks>
internal sealed class ExerciseClock
{
    private readonly DateTime _startUtc = DateTime.UtcNow;

    /// <summary>Seconds since the host started.</summary>
    public double ElapsedSeconds => (DateTime.UtcNow - _startUtc).TotalSeconds;
}

/// <summary>
/// A part of the scenario that advances on the exercise tick.
/// Implemented by the three gRPC service singletons; driven by
/// <see cref="ScenarioTicker"/>.
/// </summary>
internal interface IScenarioTick
{
    /// <summary>
    /// Move everything this part owns to <paramref name="elapsedSeconds"/>
    /// and push a frame to whoever is subscribed.
    /// </summary>
    void Tick(double elapsedSeconds);
}
