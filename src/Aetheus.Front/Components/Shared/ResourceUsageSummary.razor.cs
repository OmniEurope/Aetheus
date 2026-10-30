// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

public partial class ResourceUsageSummary
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    /// <summary>The readers of the resource; null or empty is "unused" once <see cref="Loaded"/>.</summary>
    [Parameter] public IReadOnlyList<ResourceUsageUserDto>? Users { get; set; }

    /// <summary>False until the usage call answered: nothing is drawn, never a false "Unused".</summary>
    [Parameter] public bool Loaded { get; set; }

    private IReadOnlyList<ResourceUsageUserDto> Readers => Users ?? [];

    private int TotalCount => Readers.Count;

    private string NameOf(ResourceUsageUserDto user) => NameOf(user, L);

    private string AllNames => string.Join(", ", Readers.Select(NameOf));

    private string CountLabel => string.Format(L["ResourceUsageCountLabel"], TotalCount);

    /// <summary>Two readers or more: the full list opens in a dialog, each reader a link.</summary>
    private Task OpenListAsync() =>
        Dialog.OpenAsync<ResourceUsageDialog>(
            L["ResourceUsageDialogTitle"].Value,
            new Dictionary<string, object?> { [nameof(ResourceUsageDialog.Users)] = Readers });

    /// <summary>A pipeline the caller cannot read has no name; a monitored application is named for what it does with the vault.</summary>
    internal static string NameOf(ResourceUsageUserDto user, IStringLocalizer<AppStrings> localizer) => user.Kind switch
    {
        ResourceUsageKind.MonitoredApp => string.Format(localizer["ResourceUsageWebAnalytics"], user.Name),
        _ when user.Id == 0 => localizer["ResourceUsageRestrictedPipeline"],
        _ => user.Name
    };

    internal static string? HrefOf(ResourceUsageUserDto user) => user.Kind switch
    {
        ResourceUsageKind.MonitoredApp when user.ProjectId is { } projectId => $"/projects/{projectId}/monitoring",
        ResourceUsageKind.Pipeline when user.Id > 0 => $"/pipelines/{user.Id}",
        _ => null
    };
}
