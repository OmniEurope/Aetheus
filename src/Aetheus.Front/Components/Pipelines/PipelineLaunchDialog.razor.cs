// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>Combines branch selection and queue-time parameters in one launch dialog, allowing
/// either choice independently.</summary>
public partial class PipelineLaunchDialog
{
    private readonly object _formModel = new();
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public List<GitLightBranchDto> Branches { get; set; } = [];
    [Parameter] public List<PipelineRunParameterDto> Parameters { get; set; } = [];

    /// <summary>The branch to open on - the repository default, resolved by the caller.</summary>
    [Parameter] public string? SourceBranch { get; set; }

    /// <summary>The project's restorable releases, passed through to the fields as a picking aid;
    /// <c>null</c> when no parameter takes a version.</summary>
    [Parameter] public List<ReleaseDto>? AvailableReleases { get; set; }

    /// <summary>How many releases the project can restore in all, when more exist than were loaded.</summary>
    [Parameter] public int AvailableReleaseTotal { get; set; }

    /// <summary>
    /// Re-reads the declared parameters for another branch. A branch whose YAML declares a different
    /// <c>parameters:</c> block must show its own fields, otherwise the user fills the previous
    /// branch's form and the backend rejects the run. <c>null</c> disables the re-read.
    /// </summary>
    [Parameter] public Func<string?, Task<List<PipelineRunParameterDto>?>>? ReloadParameters { get; set; }

    /// <summary>Whether the platform allows "run to end" launches; the option is shown only then.</summary>
    [Parameter] public bool RunToEndAllowed { get; set; }

    private bool _runToEnd;

    private RunParameterFields? _fields;
    private List<PipelineRunParameterDto> _parameters = [];
    private string? _branch;
    private string? _error;
    private bool _reloading;

    /// <summary>Bumped on every re-read so the fields component is rebuilt and re-seeds its inputs.</summary>
    private int _parametersRevision;

    protected override void OnInitialized()
    {
        _parameters = Parameters;
        // SourceBranch is already the resolved answer: PipelineRunDialogCoordinator applies the full
        // precedence (branch marked default, then the repository default, then the pipeline's source
        // branch, then the first branch) before opening this dialog. Recomputing a shorter version of
        // that precedence here silently overrode the caller's result whenever the two disagreed.
        _branch = SourceBranch
            ?? Branches.FirstOrDefault(branch => branch.IsDefault)?.Name
            ?? Branches.FirstOrDefault()?.Name;
    }

    private async Task OnBranchChangedAsync(string? branch)
    {
        if (string.Equals(branch, _branch, StringComparison.Ordinal)) return;
        _branch = branch;
        if (ReloadParameters is null) return;

        _reloading = true;
        _error = null;
        try
        {
            // A failed re-read keeps the previous fields rather than emptying the form: the run may
            // still be valid, and the backend stays authoritative on the declaration.
            if (await ReloadParameters(branch) is { } reloaded)
            {
                _parameters = reloaded;
                _parametersRevision++;
            }
        }
        finally
        {
            _reloading = false;
        }
    }

    private void OnSubmit()
    {
        Dictionary<string, string>? values = null;
        if (_fields is not null)
        {
            if (!_fields.TryCollect(out var collected, out _error)) return;
            values = collected.Count > 0 ? collected : null;
        }
        Dialog.Close(new PipelineLaunchChoice(_branch, values, RunToEndAllowed && _runToEnd));
    }

    private void OnCancel() => Dialog.Close(null);
}

/// <summary>What the user confirmed in <see cref="PipelineLaunchDialog"/>.</summary>
/// <param name="SourceBranch">The chosen branch, or <c>null</c> when the pipeline has no repository.</param>
/// <param name="Parameters">The effective parameter values, or <c>null</c> when none are declared.</param>
/// <param name="RunToEnd">Launch in "run to end" mode: every step continues on error.</param>
public sealed record PipelineLaunchChoice(
    string? SourceBranch, Dictionary<string, string>? Parameters, bool RunToEnd = false);
