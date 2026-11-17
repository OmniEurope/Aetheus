// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>One line of the run's parameters table: where the value comes from, its name, and its value or, for a
/// vault secret, whether it is available (the value itself never reaches the page).</summary>
public sealed record PipelineRunParameterRow(
    string Source, string? SourceHref, string Name, string? NameHref, string? Value, bool IsSecret, bool IsAvailable);

public partial class PipelineRunParametersDialog
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;

    [Parameter] public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>The run whose library and vault variables are listed with the parameters; 0 lists the parameters only.</summary>
    [Parameter] public int RunId { get; set; }

    private List<PipelineRunUsedVariableDto> _used = [];
    private bool _loadFailed;

    private List<PipelineRunParameterRow> Rows =>
    [
        .. Parameters.OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase)
            .Select(parameter => new PipelineRunParameterRow(
                L["RunParameterSource"], null, parameter.Key, null, parameter.Value, false, true)),
        .. _used.Select(Row)
    ];

    protected override async Task OnInitializedAsync()
    {
        if (RunId <= 0) return;
        try
        {
            _used = await Api.Pipelines.GetPipelineRunUsedVariablesAsync(RunId) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // The launch parameters are still listed; only the library and vault variables are missing.
            _loadFailed = true;
        }
    }

    /// <summary>The source and the variable each link to their page; the library or vault page filters on
    /// the variable name (<c>?key=</c>), which is the nearest anchor those pages support.</summary>
    internal static PipelineRunParameterRow Row(PipelineRunUsedVariableDto variable)
    {
        var page = variable.Kind == PipelineRunResourceKind.Vault ? "vaults" : "variable-libraries";
        var sourceHref = variable.SourceId is { } id ? $"/{page}/{id}" : null;
        var nameHref = sourceHref is null ? null : $"{sourceHref}?key={Uri.EscapeDataString(variable.Name)}";
        return new PipelineRunParameterRow(
            variable.SourceName, sourceHref, variable.Name, nameHref, variable.Value,
            variable.Kind == PipelineRunResourceKind.Vault, variable.IsAvailable);
    }
}
