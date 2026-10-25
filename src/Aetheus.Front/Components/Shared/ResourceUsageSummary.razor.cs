// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

public partial class ResourceUsageSummary
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The readers of the resource; null or empty is "unused" once <see cref="Loaded"/>.</summary>
    [Parameter] public IReadOnlyList<ResourceUsageUserDto>? Users { get; set; }

    /// <summary>False until the usage call answered: nothing is drawn, never a false "Unused".</summary>
    [Parameter] public bool Loaded { get; set; }

    private IReadOnlyList<ResourceUsageUserDto> Readers => Users ?? [];

    private string AllNames => string.Join(", ", Readers.Select(NameOf));

    /// <summary>A pipeline the caller cannot read has no name; a monitored application is named for what it does with the vault.</summary>
    private string NameOf(ResourceUsageUserDto user) => user.Kind switch
    {
        ResourceUsageKind.MonitoredApp => string.Format(L["ResourceUsageWebAnalytics"], user.Name),
        _ when user.Id == 0 => L["ResourceUsageRestrictedPipeline"],
        _ => user.Name
    };

    private static string? HrefOf(ResourceUsageUserDto user) => user.Kind switch
    {
        ResourceUsageKind.MonitoredApp when user.ProjectId is { } projectId => $"/projects/{projectId}/monitoring",
        ResourceUsageKind.Pipeline when user.Id > 0 => $"/pipelines/{user.Id}",
        _ => null
    };
}
