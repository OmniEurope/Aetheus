// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The pipeline setting that names the AI runner profile its <c>ai</c> steps fall back on when a step names
/// none that can run. Empty means automatic (the organization's most preferred usable profile). The list is
/// the profiles the user can see, narrowed to the pipeline's organization when it is known, most preferred
/// first; the backend refuses a profile of another organization anyway.
/// </summary>
public partial class PipelineAiProfileField
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public int? Value { get; set; }
    [Parameter] public EventCallback<int?> ValueChanged { get; set; }

    /// <summary>The organization of the pipeline's owner; null while unknown (every visible profile is offered).</summary>
    [Parameter] public int? OrganizationId { get; set; }

    private List<AiRunnerProfileDto> _profiles = [];

    internal IReadOnlyList<OmniOption<int?>> Options => _profiles
        .Where(profile => OrganizationId is null || profile.OrganizationId == OrganizationId)
        .OrderBy(profile => profile.Priority)
        .ThenBy(profile => profile.Id)
        .ToOptions(
            profile => (int?)profile.Id,
            profile => profile.NeedsReconfiguration ? $"{profile.Name} ({L["AiToReconfigure"]})" : profile.Name);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _profiles = await Api.Ai.GetAiRunnerProfileOptionsAsync();
        }
        catch (HttpRequestException)
        {
            // The pipeline stays editable: without the list the field offers "automatic" only.
            _profiles = [];
        }
    }
}
