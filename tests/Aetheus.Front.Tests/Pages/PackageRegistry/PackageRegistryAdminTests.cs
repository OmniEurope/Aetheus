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

    [Fact]
    public void RendersRegistryPackageAndEndpoints()
    {
        _handler.SetJsonResponse("api/package-registry", new PaginatedResult<PackageRegistryPackageDto>
        {
            Items = [
                new PackageRegistryPackageDto
                {
                    Id = 1,
                    Kind = PackageRegistryKind.NuGet,
                    Name = "Aetheus.Telemetry",
                    LatestVersion = "1.2.3",
                    VersionCount = 2
                }
            ],
            TotalCount = 1
        });

        var cut = Render<PackageRegistryAdmin>();

        cut.WaitForAssertion(() => Assert.Contains("Aetheus.Telemetry", cut.Markup));
        Assert.Contains("/api/packages/nuget/v3/index.json", cut.Markup);
        Assert.Contains("/api/packages/npm/", cut.Markup);
        Assert.Contains("/api/packages/maven/", cut.Markup);
        Assert.Contains("RegistryMavenHelp", cut.Markup);
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
