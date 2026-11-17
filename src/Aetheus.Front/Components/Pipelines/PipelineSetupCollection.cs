// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-26: the preparation of the collection the Supervision section of the wizard chose (the telemetry and the audience of the
/// project's monitored application), asked of the server once the pipelines exist. It never blocks the wizard: a refusal or a
/// failed call is a null result the wizard warns about. What the server did, item by item, is kept in
/// <see cref="PipelineSetupCollectionState"/> for the project's overview.
/// </summary>
internal static class PipelineSetupCollection
{
    /// <summary>Prepares what is ticked, warns when the server could not or when an item failed, and keeps the result for the
    /// project's overview (a result of nothing asked is not kept).</summary>
    public static async Task PrepareAndTellAsync(
        PipelinesApi pipelines, PipelineSetupCollectionState state, NotifyHelper toast, int projectId, bool telemetry, bool audience)
    {
        var result = await PrepareAsync(pipelines, projectId, telemetry, audience);
        if (result is null || result.HasFailure)
            toast.Warning("TelemetryNotConfigured", "TelemetryNotConfiguredDetail");
        if (result is not null)
            state.Record(projectId, result);
    }

    /// <summary>Asks the server to prepare what is <paramref name="telemetry"/> and <paramref name="audience"/> (each ticked and
    /// offered). An empty result when nothing was asked; null (never an exception from a refusal) when the server could not.</summary>
    public static async Task<PipelineSetupCollectionResultDto?> PrepareAsync(
        PipelinesApi pipelines, int projectId, bool telemetry, bool audience)
    {
        if (!telemetry && !audience) return new PipelineSetupCollectionResultDto();
        try
        {
            return await pipelines.PrepareSetupCollectionAsync(
                new PipelineSetupCollectionRequest { ProjectId = projectId, Telemetry = telemetry, Audience = audience });
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
