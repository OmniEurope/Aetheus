// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Components.Environments;

/// <summary>
/// The expand/contract tracking of the database of an environment (recette R-18), on the environment page's own tab
/// and, grouped by environment, on the project overview (R-10). Renders nothing when the back cannot answer: the
/// page around it does not depend on it.
/// </summary>
public partial class EnvironmentSchemaTracking
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int EnvironmentId { get; set; }

    /// <summary>
    /// The answer, when the host already read it (the project overview reads every environment of the project
    /// at once to order them and to head each group). Null, the default, has the component read its own.
    /// </summary>
    [Parameter] public EnvironmentSchemaDto? Schema { get; set; }

    /// <summary>False when the host already names the environment above the block (the project overview).</summary>
    [Parameter] public bool ShowTitle { get; set; } = true;

    private EnvironmentSchemaDto? _schema;
    private int? _loadedFor;

    protected override async Task OnParametersSetAsync()
    {
        if (Schema is not null)
        {
            _schema = Schema;
            _loadedFor = EnvironmentId;
            return;
        }
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
