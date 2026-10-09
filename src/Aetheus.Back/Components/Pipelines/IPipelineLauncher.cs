// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

public interface IPipelineLauncher
{
    Task<PipelineRunDto?> TriggerRunAsync(int id, Dictionary<string, string>? additionalVariables = null,
        Dictionary<string, string>? parameters = null, CancellationToken ct = default);

    /// <summary>Launches exactly <paramref name="preparation"/>, which the caller resolved and authorized
    /// (BACK-023: the release routes check the same rights as the direct pipeline route on it).</summary>
    Task<PipelineRunDto?> TriggerPreparedRunAsync(PipelineRunPreparation preparation,
        Dictionary<string, string>? additionalVariables = null,
        Dictionary<string, string>? parameters = null, CancellationToken ct = default,
        string? idempotencyKey = null);
}
