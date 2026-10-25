// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.PackageRegistry;
using Bunit;

namespace Aetheus.Front.Tests.Pages.PackageRegistry;

public sealed class PackageRegistryPackageDialogTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PackageRegistryPackageDialogTests() =>
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    [Fact]
    public void RendersVersionListingState()
    {
        _handler.SetJsonResponse("api/package-registry/7", new PackageRegistryPackageDetailDto
        {
            Id = 7,
            Kind = PackageRegistryKind.Npm,
            Name = "@aetheus/browser",
            Versions = [
                new PackageRegistryVersionDto
                {
                    Id = 9,
                    Version = "2.0.0",
                    IsListed = true,
                    SizeBytes = 2048,
                    CreatedAt = DateTime.UtcNow
                }
            ]
        });

        var cut = Render<PackageRegistryPackageDialog>(parameters =>
            parameters.Add(component => component.PackageId, 7));

        cut.WaitForAssertion(() => Assert.Contains("2.0.0", cut.Markup));
        Assert.Contains("Unlist", cut.Markup);
        Assert.Contains("2 Ko", cut.Markup);
    }

    private IRenderedComponent<PackageRegistryPackageDialog> RenderWithVersion(bool deprecated)
    {
        _handler.SetJsonResponse("api/package-registry/7", new PackageRegistryPackageDetailDto
        {
            Id = 7,
            Kind = PackageRegistryKind.NuGet,
            Name = "Aetheus.Telemetry",
            Versions = [
                new PackageRegistryVersionDto
                {
                    Id = 9, Version = "1.0.0", IsListed = true, IsDeprecated = deprecated, SizeBytes = 10,
                    CreatedAt = DateTime.UtcNow
                }
            ]
        });
        _handler.SetResponse("api/package-registry/7/versions/9", System.Net.HttpStatusCode.NoContent);
        var cut = Render<PackageRegistryPackageDialog>(parameters =>
            parameters.Add(component => component.PackageId, 7));
        cut.WaitForAssertion(() => Assert.Contains("1.0.0", cut.Markup));
        return cut;
    }

    // R1-18: Unlist and Deprecate are both orange (user-chosen exception), one Deprecate per version.
    [Fact]
    public void EachVersion_OffersDeprecate_AndBothActionsAreWarningTone()
    {
        var cut = RenderWithVersion(deprecated: false);

        var deprecate = cut.Find("button[title=Deprecate]");
        var unlist = cut.Find("button[title=Unlist]");
        Assert.Contains("omni-button--warning", deprecate.ClassName);
        Assert.Contains("omni-button--warning", unlist.ClassName);
        Assert.Empty(cut.FindAll("td .omni-badge--warning"));
    }

    [Fact]
    public void Deprecate_SendsTheDeprecationRequest_ForThatVersion()
    {
        var cut = RenderWithVersion(deprecated: false);

        cut.Find("button[title=Deprecate]").Click();

        cut.WaitForAssertion(() =>
        {
            var put = Assert.Single(_handler.RequestDetails, request => request.Method == "PUT");
            Assert.EndsWith("api/package-registry/7/versions/9/deprecation", put.Url);
            Assert.Contains("\"isDeprecated\":true", put.Body, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void DeprecatedVersion_ShowsABadge_AndOffersToRemoveTheDeprecation()
    {
        var cut = RenderWithVersion(deprecated: true);

        Assert.Single(cut.FindAll("td .omni-badge--warning"));
        cut.Find("button[title=Undeprecate]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("\"isDeprecated\":false", _handler.RequestDetails.Single(r => r.Method == "PUT").Body,
                StringComparison.OrdinalIgnoreCase));
    }
}
