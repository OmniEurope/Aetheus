// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class ReleasePickerGrid
{
    /// <summary>The grid requests releases in blocks as the user scrolls.</summary>
    internal const int PageSize = 20;

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<ReleaseDto> Releases { get; set; } = [];

    /// <summary>How many releases the project can restore in all; above <see cref="Releases"/>'s count
    /// the grid says it shows only the most recent ones rather than letting the rest vanish.</summary>
    [Parameter] public int TotalCount { get; set; }

    /// <summary>Raised with the full version of the clicked row; unset when no field can take it.</summary>
    [Parameter] public EventCallback<string> Pick { get; set; }

    /// <summary>The version currently in the field, so the card list can show which release is chosen.</summary>
    [Parameter] public string? SelectedVersion { get; set; }

    private List<ReleaseDto> _page = [];
    private int _count;

    // By default only the releases worth deploying are listed; "Tout afficher" lists them all.
    private bool _showAll;

    // The two status columns filter by a checkable list of their members.
    private Func<string, string>? _statusText;
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<ReleaseStatus>(L);
    private Func<string, string>? _runStatusText;
    private Func<string, string> RunStatusText => _runStatusText ??= GridFilterText.ForEnum<PipelineStatus>(L);
    private Func<string, string>? _gradeText;
    private Func<string, string> GradeText => _gradeText ??= GridFilterText.ForEnum<AnalysisGrade>(L);

    /// <summary>The releases offered now: those worth deploying, or every one after "Tout afficher".</summary>
    private IReadOnlyList<ReleaseDto> Visible =>
        _showAll ? Releases : Releases.Where(ReleasePickerVisibility.IsDefaultVisible).ToList();

    private int HiddenCount => Releases.Count - Releases.Count(ReleasePickerVisibility.IsDefaultVisible);

    private void ToggleShowAll() => _showAll = !_showAll;

    private Task PickCardAsync(string? version) =>
        version is null ? Task.CompletedTask : Pick.InvokeAsync(version);

    /// <summary>The grid's window (or, in a host without virtualization, its page) is cut from the loaded
    /// list here, after its sort and filters (a wrapper grid always asks through LoadData), so the header
    /// affordances act on every release, not on the rows on screen.</summary>
    private void LoadPage(GridLoadArgs args)
    {
        var visible = Visible;
        _page = args.ToClientPage(visible, PageSize);
        _count = args.ClientFilteredCount(visible);
    }
}
