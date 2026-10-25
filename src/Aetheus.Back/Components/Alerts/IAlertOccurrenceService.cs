// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Alerts;

/// <summary>Gives every alert that fires an identifier of its own, which is its address in the app (recette R-52).</summary>
public interface IAlertOccurrenceService
{
    /// <summary>Stores the occurrence and returns its identifier, or null when it could not be stored: the alert
    /// is still emitted then, only without its page, since losing an alert is worse than losing its link.</summary>
    Task<long?> RecordAsync(NewAlertOccurrence occurrence, CancellationToken ct = default);

    /// <summary>The occurrence, or null when no alert has this identifier.</summary>
    Task<AlertOccurrenceDto?> GetAsync(long id, CancellationToken ct = default);
}
