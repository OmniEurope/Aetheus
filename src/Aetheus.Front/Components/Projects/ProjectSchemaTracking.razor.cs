// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Components.Projects;

/// <summary>
/// The "Expand/contract" tab of the project overview (recette R-10): the tracking of every environment of the
/// project, production first. The back has no project-level answer: the contraction plan is global to the code and
/// only the last migration gate belongs to an environment, so each environment is asked through the endpoint of its
/// own page, which checks the caller's access to that environment (and to the pipeline of its verdict).
/// </summary>
public partial class ProjectSchemaTracking
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>One environment and what the back answered for it; a null schema is an answer that could not be read.</summary>
    internal sealed record EnvironmentGroup(EnvironmentDto Environment, EnvironmentSchemaDto? Schema)
    {
        public bool IsProduction => Environment.Type == EnvironmentType.Production;
    }

    private List<EnvironmentGroup>? _groups;
    private int? _loadedFor;

    /// <summary>The contraction plan is the same for every environment with a verdict: the largest list is the count.</summary>
    private int PendingCount => _groups!.Max(group => group.Schema?.LastGate is null ? 0 : group.Schema.PendingContractions.Count);

    private int WithVerdictCount => _groups!.Count(group => group.Schema?.LastGate is not null);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == ProjectId) return;
        _loadedFor = ProjectId;
        _groups = null;
        var environments = await Api.Servers.GetAllEnvironmentsAsync(ProjectId);
        var schemas = await Task.WhenAll(environments.Select(ReadSchemaAsync));
        _groups =
        [
            .. environments.Zip(schemas, (environment, schema) => new EnvironmentGroup(environment, schema))
                .OrderBy(group => group.IsProduction ? 0 : 1)
                .ThenBy(group => group.Environment.Name, StringComparer.CurrentCultureIgnoreCase)
        ];
    }

    private async Task<EnvironmentSchemaDto?> ReadSchemaAsync(EnvironmentDto environment)
    {
        try
        {
            return await Api.Pipelines.GetEnvironmentSchemaTrackingAsync(environment.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private static OmniTone OutcomeTone(MigrationGateOutcome outcome) => PipelineRunMigrationGate.OutcomeTone(outcome);
}
