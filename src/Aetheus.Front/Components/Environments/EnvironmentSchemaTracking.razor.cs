// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Components.Environments;

/// <summary>
/// The expand/contract tracking of the database on a production environment's page (recette R-18). Renders
/// nothing when the back cannot answer: the environment form above it does not depend on it.
/// </summary>
public partial class EnvironmentSchemaTracking
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int EnvironmentId { get; set; }

    private EnvironmentSchemaDto? _schema;
    private int? _loadedFor;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == EnvironmentId) return;
        _loadedFor = EnvironmentId;
        try
        {
            _schema = await Api.Pipelines.GetEnvironmentSchemaTrackingAsync(EnvironmentId);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _schema = null;
        }
    }

    private static string RunHref(PipelineRunMigrationGateDto gate) => $"/pipelines/runs/{gate.RunId}?tab=schema";
}
