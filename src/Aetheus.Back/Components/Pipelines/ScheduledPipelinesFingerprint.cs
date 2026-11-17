// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// A cheap summary of the pipelines whose trigger is <c>schedule</c>: how many there are, the latest
/// <c>UpdatedAt</c> among them, and the sum of their ids. The scheduler compares it with the one it
/// last loaded the pipelines under, and reloads only when it differs. The count sees an added, removed
/// or disabled pipeline, the latest update sees an edited definition, and the id sum sees a removal
/// paired with an addition that carries the same count and the same latest update.
/// </summary>
public readonly record struct ScheduledPipelinesFingerprint(int Count, DateTime? LatestUpdate, long IdSum);
