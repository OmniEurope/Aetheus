// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Alerts;

public interface IAlertOccurrenceRepository
{
    Task AddAsync(AlertOccurrence occurrence, CancellationToken ct = default);

    /// <summary>The occurrence with its rule, read without tracking; null when no occurrence has this identifier.</summary>
    Task<AlertOccurrence?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>Deletes the occurrences triggered strictly before the cutoff; returns how many were deleted.</summary>
    Task<int> DeleteTriggeredBeforeAsync(DateTime cutoffUtc, CancellationToken ct = default);
}
