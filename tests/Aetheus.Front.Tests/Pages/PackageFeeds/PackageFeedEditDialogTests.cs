// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.PackageFeeds;
using Bunit;

namespace Aetheus.Front.Tests.Pages.PackageFeeds;

public sealed class PackageFeedEditDialogTests : BunitContext
{
    private const string UpstreamBoxId = "oe-pages-packagefeeds-packagefeededitdialog-3";

    public PackageFeedEditDialogTests()
        => BunitTestHelper.RegisterServices(this, isAdmin: true);

    [Fact]
    public void CreationDialog_OffersOnlyImplementedFeedTypesAndExplainsTheBoundary()
    {
        var cut = Render<PackageFeedEditDialog>();
        Assert.Equal(4, PackageFeedEditDialog.SupportedCreationTypeCount);
        Assert.Contains("PackageFeedCreationSupportedTypes", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosingAType_PrefillsItsPublicRegistry_AndATypeWithoutOneKeepsTheUrl()
    {
        var cut = Render<PackageFeedEditDialog>();
        var type = cut.FindComponent<OmniDropDown<PackageFeedType>>();
        string? Upstream() => cut.FindComponents<OmniTextBox>().Single(box => box.Instance.Id == UpstreamBoxId).Instance.Value;

        await cut.InvokeAsync(() => type.Instance.ValueChanged.InvokeAsync(PackageFeedType.Npm));
        Assert.Equal(PackageRegistryDefaults.Npm, Upstream());

        await cut.InvokeAsync(() => type.Instance.ValueChanged.InvokeAsync(PackageFeedType.PyPI));
        Assert.Equal(PackageRegistryDefaults.PyPi, Upstream());

        await cut.InvokeAsync(() => type.Instance.ValueChanged.InvokeAsync(PackageFeedType.NuGet));
        Assert.Equal(PackageRegistryDefaults.NuGet, Upstream());

        await cut.InvokeAsync(() => type.Instance.ValueChanged.InvokeAsync(PackageFeedType.Maven));
        Assert.Equal(PackageRegistryDefaults.Maven, Upstream());

        await cut.InvokeAsync(() => type.Instance.ValueChanged.InvokeAsync(PackageFeedType.Docker));
        Assert.Equal(PackageRegistryDefaults.Maven, Upstream());
    }
}
