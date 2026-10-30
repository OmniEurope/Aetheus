// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Reviews a pipeline definition before its Git-authoritative update is committed. The review
/// compares against the repository's own file on the chosen branch, never against Aetheus's copy alone:
/// Aetheus must not overwrite a repository change it has not seen.</summary>
public partial class PipelineSaveDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;

    [Parameter] public int PipelineId { get; set; }
    [Parameter] public string? PipelineName { get; set; }
    [Parameter, EditorRequired] public string SavedYaml { get; set; } = string.Empty;
    [Parameter, EditorRequired] public string DraftYaml { get; set; } = string.Empty;
    [Parameter] public List<string> Branches { get; set; } = [];
    [Parameter] public string? SourceBranch { get; set; }

    private string? _branch;
    private bool _loading = true;
    private PipelineRepositoryDefinitionDto? _repository;
    private int _loadGeneration;

    private bool Edited => !Same(SavedYaml, DraftYaml);
    private bool RepositoryHoldsAFile => _repository is { Exists: true, Yaml: not null };
    private bool RepositoryChangedSinceSync => RepositoryHoldsAFile && !Same(_repository!.Yaml, SavedYaml);

    /// <summary>The definition is not edited and the repository has its own file: that file is kept as
    /// the definition and nothing is committed.</summary>
    private bool AdoptsRepositoryFile => !Edited && RepositoryHoldsAFile;

    /// <summary>The edit would replace lines of the repository file Aetheus has not seen.</summary>
    private bool OverwritesRepositoryChange => Edited && RepositoryChangedSinceSync && !Same(_repository!.Yaml, DraftYaml);

    private string DiffOriginal => RepositoryChangedSinceSync && Edited ? _repository!.Yaml! : SavedYaml;
    private string DiffModified => AdoptsRepositoryFile ? _repository!.Yaml! : DraftYaml;

    /// <summary>Opens the review for the form <paramref name="model"/> against the definition last saved;
    /// null when the user cancels.</summary>
    internal static async Task<PipelineSaveDecision?> ReviewAsync(
        OmniDialogService dialog, string title, int pipelineId, PipelineModel model, string savedYaml,
        List<string> branches) =>
        await dialog.OpenAsync<PipelineSaveDialog>(
            title,
            new Dictionary<string, object?>
            {
                { nameof(PipelineId), pipelineId },
                { nameof(PipelineName), model.Name },
                { nameof(SavedYaml), savedYaml },
                { nameof(DraftYaml), model.YamlDefinition },
                { nameof(Branches), branches },
                { nameof(SourceBranch), model.SourceBranch }
            },
            new OmniDialogOptions { Width = "900px", Height = "720px", AutoFocusFirstElement = false }) as PipelineSaveDecision;

    protected override async Task OnInitializedAsync()
    {
        _branch = SourceBranch ?? Branches.FirstOrDefault();
        await LoadRepositoryAsync();
    }

    private async Task OnBranchChangedAsync(string? branch)
    {
        _branch = branch;
        await LoadRepositoryAsync();
    }

    private async Task LoadRepositoryAsync()
    {
        var generation = ++_loadGeneration;
        _loading = true;
        var repository = PipelineId > 0
            ? await Api.Pipelines.GetRepositoryDefinitionAsync(PipelineId, _branch, PipelineName)
            : new PipelineRepositoryDefinitionDto();
        if (generation != _loadGeneration) return;
        _repository = repository;
        _loading = false;
    }

    private void Confirm() => Dialog.Close(new PipelineSaveDecision(
        _branch, OverwritesRepositoryChange ? _repository!.Sha256 : null));

    private void Cancel() => Dialog.Close(null);

    // Line endings and trailing blank lines carry no meaning for a definition (as on the server).
    private static bool Same(string? left, string? right) => Normalize(left) == Normalize(right);

    private static string Normalize(string? yaml) =>
        (yaml ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
}

/// <param name="SourceBranch">The branch the definition is committed to.</param>
/// <param name="ConfirmedRepositoryYamlSha256">The hash of the repository file the user reviewed when the
/// save replaces lines Aetheus has not seen; null otherwise.</param>
public sealed record PipelineSaveDecision(string? SourceBranch, string? ConfirmedRepositoryYamlSha256 = null);
