// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The per-file coverage tree loads once when the Coverage tab opens, keeping live run
/// refreshes small. An already populated summary supplies its files directly.</summary>
public partial class PipelineRunCoverageFiles
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineCoverageSummaryDto Summary { get; set; } = default!;

    private IReadOnlyList<CoverageFileDto> _files = [];
    private IReadOnlyList<CoverageProjectTree.Project> _projects = [];
    private int? _loadedRunId;
    private bool _loading;

    protected override async Task OnParametersSetAsync()
    {
        if (Summary.Files.Count > 0)
        {
            Show(Summary.Files);
            return;
        }
        if (Summary.RunId is not { } runId || runId == _loadedRunId) return;

        _loadedRunId = runId;
        _loading = true;
        try
        {
            var full = await Api.Analysis.GetCoverageSummaryAsync(runId);
            Show(full?.Files ?? []);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Show(IReadOnlyList<CoverageFileDto> files)
    {
        if (ReferenceEquals(files, _files)) return;
        _files = files;
        _projects = CoverageProjectTree.Build(files);
    }

    /// <summary>A project's or a file's line in the coverage tree, name then figures.</summary>
    private string CoverageTreeText(string name, double rate, int covered, int valid) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, L["CoverageTreeEntry"], name, PipelineRunFormatting.FormatPercent(rate), covered, valid);
}
