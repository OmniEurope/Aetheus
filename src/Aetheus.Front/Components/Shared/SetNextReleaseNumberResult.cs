// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>The answer of fixing a series' next release number: the series with its new next number, or
/// <see cref="NumberAlreadyUsed"/> when a run already took that number (HTTP 409). Both empty means another
/// failure, already announced by the global notifier.</summary>
public sealed record SetNextReleaseNumberResult(ProjectReleaseSeriesDto? Series, bool NumberAlreadyUsed);
