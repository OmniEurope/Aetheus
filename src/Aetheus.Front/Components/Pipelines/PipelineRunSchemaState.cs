// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The expand/contract tracking of a run (recette R-18), read with the run so the page knows whether its
/// Schema tab exists: a run that no migrate step reported on has none.
/// </summary>
internal sealed class PipelineRunSchemaState
{
    public PipelineRunSchemaDto? Schema { get; private set; }

    /// <summary>The tab is shown only for a run that has a gate record.</summary>
    public bool HasGate => Schema is { Gates.Count: > 0 };

    public void Clear() => Schema = null;

    public async Task LoadAsync(ApiClient api, int runId, CancellationToken ct = default)
    {
        try
        {
            Schema = await api.Pipelines.GetPipelineRunSchemaTrackingAsync(runId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // The step log still carries the gate's lines; only this tab is missing.
            Schema = null;
        }
    }
}
