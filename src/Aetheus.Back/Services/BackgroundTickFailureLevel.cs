// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services;

/// <summary>
/// The level a leader worker's failed tick is logged at (recette R-2, decision of 2026-10-29: a restart is never a
/// fatal). A tick that fails because the database is restarting or cannot be reached is not a fault of the worker:
/// it is told at information level when the server ended the session on administrator command, and as one warning
/// per outage when the database cannot be reached (the ticks that follow, every few seconds, are debug lines until
/// a tick succeeds). Anything else stays an error, so a real failure is never taken for a restart.
/// </summary>
internal sealed class BackgroundTickFailureLevel
{
    private LeaderInterruption? _last;

    /// <summary>The level for a tick that failed with <paramref name="failure"/>.</summary>
    public LogLevel For(Exception failure, CancellationToken stoppingToken)
    {
        var kind = LeaderInterruptionClassifier.Classify(failure, stoppingToken);
        var repeated = _last == kind;
        _last = kind;
        return kind switch
        {
            LeaderInterruption.DatabaseStoppedSession or LeaderInterruption.HostStopping => LogLevel.Information,
            LeaderInterruption.DatabaseUnavailable => repeated ? LogLevel.Debug : LogLevel.Warning,
            _ => LogLevel.Error
        };
    }

    /// <summary>A tick succeeded: the next outage is told again.</summary>
    public void Succeeded() => _last = null;
}
