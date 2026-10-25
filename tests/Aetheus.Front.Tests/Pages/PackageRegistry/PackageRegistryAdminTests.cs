// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.PackageRegistry;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.PackageRegistry;

public sealed class PackageRegistryAdminTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PackageRegistryAdminTests() =>
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    private void SeedOnePackage() =>
        _handler.SetJsonResponse("api/package-registry", new PaginatedResult<PackageRegistryPackageDto>
        {
            Items = [
                new PackageRegistryPackageDto
                {
                    Id = 1,
                    Kind = PackageRegistryKind.NuGet,
                    Name = "Aetheus.Telemetry",
                    LatestVersion = "1.2.3",
                    VersionCount = 2,
                    UpdatedAt = new DateTime(2026, 10, 21, 14, 35, 0)
                }
            ],
            TotalCount = 1
        });

    // R1-14: tabs replace the three tiles and the header search; the addresses live in their own tab.
    [Fact]
    public void RendersPackageGridAndTabs_WithoutTilesOrSearch()
    {
        SeedOnePackage();

        var cut = Render<PackageRegistryAdmin>();

        cut.WaitForAssertion(() => Assert.Contains("Aetheus.Telemetry", cut.Markup));
        var tabs = cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()).ToList();
        Assert.Equal(["All", "NuGet", "npm", "Maven", "PackageRegistryAddresses"], tabs);
        Assert.Empty(cut.FindAll(".omni-card"));
        Assert.Empty(cut.FindAll("[aria-label=Search]"));
        Assert.DoesNotContain("/api/packages/nuget/v3/index.json", cut.Markup);
    }

    [Fact]
    public void AddressesTab_ShowsTheThreeSourceAddresses_AndNoGrid()
    {
        SeedOnePackage();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/admin/package-registry?tab=addresses");

        var cut = Render<PackageRegistryAdmin>();

        cut.WaitForAssertion(() => Assert.Contains("/api/packages/nuget/v3/index.json", cut.Markup));
        Assert.Contains("/api/packages/npm/", cut.Markup);
        Assert.Contains("/api/packages/maven/", cut.Markup);
        Assert.Contains("RegistryMavenHelp", cut.Markup);
        Assert.Empty(cut.FindAll(".aetheus-data-grid"));
    }

    [Fact]
    public void TypeTab_ListsOnlyThatType_AndHidesTheTypeColumn()
    {
        SeedOnePackage();
        var cut = Render<PackageRegistryAdmin>();
        cut.WaitForAssertion(() => Assert.Contains("Aetheus.Telemetry", cut.Markup));
        Assert.Contains("package-kind-badge--nuget", cut.Markup);

        cut.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == "npm").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request =>
            request.Method == "GET" && request.Url.Contains("kind=Npm", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.DoesNotContain("package-kind-badge--nuget", cut.Markup));
    }

    // R1-15: Name / Version titles, no version count column, the date without the time.
    [Fact]
    public void Grid_UsesNameAndVersionTitles_NoVersionCount_AndDateWithoutTime()
    {
        SeedOnePackage();

        var cut = Render<PackageRegistryAdmin>();

        cut.WaitForAssertion(() => Assert.Contains("Aetheus.Telemetry", cut.Markup));
        var headers = cut.FindAll(".omni-data-grid__title").Select(title => title.TextContent.Trim()).ToList();
        Assert.Contains("Name", headers);
        Assert.Contains("Version", headers);
        Assert.DoesNotContain("Versions", headers);
        Assert.DoesNotContain("PackageName", headers);
        Assert.DoesNotContain("LatestVersion", headers);
        var expectedDate = new DateTime(2026, 10, 21).ToString("d");
        Assert.Contains(expectedDate, cut.Markup);
        Assert.DoesNotContain(new DateTime(2026, 10, 21, 14, 35, 0).ToString("g"), cut.Markup);
    }

    // R1-16: one row action, "View details", Primary, centered in its column.
    [Fact]
    public void RowAction_IsASinglePrimaryViewDetailsButton_InACenteredColumn()
    {
        SeedOnePackage();

        var cut = Render<PackageRegistryAdmin>();

        cut.WaitForAssertion(() => Assert.Contains("Aetheus.Telemetry", cut.Markup));
        var buttons = cut.FindAll("td button[title=ViewDetails]");
        var button = Assert.Single(buttons);
        Assert.Contains("omni-button--primary", button.ClassName);
        Assert.Contains("omni-data-grid__column--align-center", button.Closest("td")!.ClassName);
    }

    // R1-19: the type badge carries the brand mark and colour class.
    [Theory]
    [InlineData(PackageRegistryKind.NuGet, "nuget")]
    [InlineData(PackageRegistryKind.Npm, "npm")]
    [InlineData(PackageRegistryKind.Maven, "maven")]
    public void KindBadge_CarriesTheBrandClassAndAGlyph(PackageRegistryKind kind, string slug)
    {
        var cut = Render<PackageRegistryKindBadge>(parameters => parameters.Add(badge => badge.Kind, kind));

        Assert.Contains($"package-kind-badge--{slug}", cut.Find(".omni-badge").ClassName);
        Assert.NotEmpty(cut.FindAll(".omni-badge svg path"));
        Assert.Contains(kind.ToString(), cut.Markup);
    }

    [Fact]
    public void EmptyRegistry_RendersExplicitEmptyState()
    {
        _handler.SetJsonResponse(
            "api/package-registry",
            new PaginatedResult<PackageRegistryPackageDto>());

        var cut = Render<PackageRegistryAdmin>();

        cut.WaitForAssertion(() => Assert.Contains("NoRegistryPackages", cut.Markup));
    }

    [Fact]
    public void NonAdmin_IsRedirectedWithoutRegistryRequest()
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: false);
        var cut = Render<PackageRegistryAdmin>();

        Assert.DoesNotContain(handler.Requests, request =>
            request.Url.Contains("api/package-registry", StringComparison.Ordinal));
        Assert.Equal("/", new Uri(Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri).AbsolutePath);
        Assert.Empty(cut.FindAll(".aetheus-data-grid"));
    }
}
