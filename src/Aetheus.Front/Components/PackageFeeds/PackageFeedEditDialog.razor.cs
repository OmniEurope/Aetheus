// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.PackageFeeds;

public partial class PackageFeedEditDialog : EntityEditDialogBase
{
    private readonly EditModel _model = new();

    private static readonly List<OmniOption<PackageFeedType>> _types = Enum.GetValues<PackageFeedType>()
        .Where(IsCreationSupported)
        .Select(t => new OmniOption<PackageFeedType>(t, t.ToString())).ToList();
    internal static int SupportedCreationTypeCount => _types.Count;

    private static bool IsCreationSupported(PackageFeedType type)
        => type is PackageFeedType.NuGet or PackageFeedType.Npm or PackageFeedType.PyPI or PackageFeedType.Maven;

    // Prefill the upstream URL with the public registry default for the chosen type (editable).
    private void OnTypeChanged() => _model.UpstreamUrl = _model.FeedType switch
    {
        PackageFeedType.NuGet => PackageRegistryDefaults.NuGet,
        PackageFeedType.Npm => PackageRegistryDefaults.Npm,
        PackageFeedType.PyPI => PackageRegistryDefaults.PyPi,
        PackageFeedType.Maven => PackageRegistryDefaults.Maven,
        _ => _model.UpstreamUrl
    };

    private Task SubmitAsync() => RunBusyAsync(() => Ui.RunAsync(
        () => Api.Packages.CreatePackageFeedAsync(new CreatePackageFeedRequest
        {
            Name = _model.Name,
            Description = _model.Description,
            FeedType = _model.FeedType,
            UpstreamUrl = _model.UpstreamUrl
        }),
        "Created",
        _ => CloseAfterSuccessAsync(),
        successTitleKey: "Created"));

    private sealed class EditModel
    {
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public PackageFeedType FeedType { get; set; } = PackageFeedType.NuGet;

        [Required]
        [StringLength(500)]
        public string UpstreamUrl { get; set; } = PackageRegistryDefaults.NuGet;

        [StringLength(500)]
        public string? Description { get; set; }
    }
}
