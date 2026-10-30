// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using Aetheus.Front.Components.VariableLibraries;
using Aetheus.Front.Components.Vaults;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Recette R-65, R-66, R-67, R-70: a library or a vault says what reads it (or that nothing
/// does), its tools sit in the page header, and deleting it returns to the page the user came from.</summary>
public sealed class ResourceUsageTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ResourceUsageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "aetheus" }],
            TotalCount = 1
        });
    }

    private static ResourceUsageUserDto Pipeline(int id, string name) =>
        new() { Kind = ResourceUsageKind.Pipeline, Id = id, Name = name, ProjectId = 1 };

    private void MockVault(int id = 1)
    {
        _handler.SetJsonResponse($"api/vaults/{id}", new VaultDetailDto
        {
            Id = id,
            Name = "prod-vault",
            ProjectId = 1,
            RowVersion = Guid.NewGuid(),
            Secrets = [new VaultSecretDto { Id = 10, Key = "DB_PASS" }]
        });
    }

    private void MockLibrary(int id = 1)
    {
        _handler.SetJsonResponse($"api/variable-libraries/{id}", new VariableLibraryDetailDto
        {
            Id = id,
            Name = "aetheus.prod",
            ProjectId = 1,
            RowVersion = Guid.NewGuid(),
            EntryCount = 1
        });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, $"api/variable-libraries/{id}/entries",
            [new VariableEntryDto { Id = 1, Key = "DOMAIN", Value = "example.com" }]);
    }

    [Fact]
    public void AVaultList_NamesTheReadersAsLinks_AndFlagsAnUnusedVault_AndRemembersItselfForTheReturn()
    {
        _handler.SetJsonResponse("api/vaults", new PaginatedResult<VaultDto>
        {
            Items =
            [
                new VaultDto { Id = 1, Name = "used-vault", ProjectId = 1 },
                new VaultDto { Id = 2, Name = "orphan-vault", ProjectId = 1 }
            ],
            TotalCount = 2
        });
        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Vaults = new() { [1] = [Pipeline(10, "deploy-prod")] }
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/projects/1/vaults");

        var cut = Render<VaultsList>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForAssertion(() => Assert.Contains("deploy-prod", cut.Markup));

        Assert.NotNull(cut.Find("a[href='/pipelines/10']"));
        // Only the vault nothing reads carries the badge.
        Assert.Single(cut.FindAll(".omni-badge"), badge => badge.TextContent.Contains("ResourceUnused", StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("a.pipeline-name-link"),
            link => link.GetAttribute("href") == "/vaults/1?returnUrl=%2Fprojects%2F1%2Fvaults");
    }

    [Fact]
    public void AVaultListWhoseUsageCallFails_ClaimsNothingIsUnused()
    {
        _handler.SetJsonResponse("api/vaults", new PaginatedResult<VaultDto>
        {
            Items = [new VaultDto { Id = 1, Name = "used-vault", ProjectId = 1 }],
            TotalCount = 1
        });
        _handler.SetResponse("api/pipelines/resource-usage", HttpStatusCode.InternalServerError);

        var cut = Render<VaultsList>();
        cut.WaitForAssertion(() => Assert.Contains("used-vault", cut.Markup));

        Assert.DoesNotContain("ResourceUnused", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ALibraryList_NamesTheReadersAndFlagsAnUnusedLibrary()
    {
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto>
        {
            Items =
            [
                new VariableLibraryDto { Id = 1, Name = "aetheus.prod", ProjectId = 1 },
                new VariableLibraryDto { Id = 2, Name = "aetheus-public", ProjectId = 1 }
            ],
            TotalCount = 2
        });
        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Libraries = new() { [1] = [Pipeline(10, "deploy-prod"), Pipeline(11, "release-fast")] }
        });

        var host = Render<OmniComponentsHost>(p => p.Add(h => h.OverlayService, Services.GetRequiredService<OmniOverlayService>()));
        var cut = Render<VariableLibrariesList>();
        // Two readers (R-103): the cell shows their total, the dialog names each as a link.
        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("button.omni-badge[data-testid='resource-usage-count']").TextContent.Trim()));
        Assert.DoesNotContain("release-fast", cut.Markup, StringComparison.Ordinal);
        cut.Find("button[data-testid='resource-usage-count']").Click();
        host.WaitForAssertion(() => Assert.NotNull(host.Find(".omni-dialog a[href='/pipelines/11']")));
        Assert.NotNull(host.Find(".omni-dialog a[href='/pipelines/10']"));
        Assert.Single(cut.FindAll(".omni-badge"), badge => badge.TextContent.Contains("ResourceUnused", StringComparison.Ordinal));
    }

    [Fact]
    public void TheVaultPage_SaysWhatReadsIt_AMonitoredApplicationLinkingToItsMonitoring()
    {
        MockVault();
        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Vaults = new()
            {
                [1] = [Pipeline(10, "demo"), new ResourceUsageUserDto { Kind = ResourceUsageKind.MonitoredApp, Id = 40, Name = "Site", ProjectId = 1 }]
            }
        });

        var host = Render<OmniComponentsHost>(p => p.Add(h => h.OverlayService, Services.GetRequiredService<OmniOverlayService>()));
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        // Two readers (R-103): the line shows their total; the dialog lists both.
        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find(".resource-usage-line .omni-badge").TextContent.Trim()));
        cut.Find(".resource-usage-line button").Click();

        host.WaitForAssertion(() => Assert.NotNull(host.Find(".omni-dialog a[href='/pipelines/10']")));
        Assert.NotNull(host.Find(".omni-dialog a[href='/projects/1/monitoring']"));
        Assert.Contains("ResourceUsageWebAnalytics", host.Find(".omni-dialog").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVaultPage_FlagsAnUnusedVault()
    {
        MockVault();

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));

        cut.WaitForAssertion(() => Assert.Contains("ResourceUnused", cut.Find(".resource-usage-line").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void TheVaultTools_AreTheHeaderMenu_NotPartOfTheAddSecretRow()
    {
        MockVault();

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));

        Assert.Empty(cut.FindAll(".vault-add-row .omni-overflow-menu__trigger"));
        Assert.NotEmpty(cut.FindAll(".omni-overflow-menu__trigger"));
        // Key, Value and the expiry share the fields line; Generate and Add the actions line.
        Assert.Equal(3, cut.Find(".vault-add-row__fields").Children.Length);
        Assert.Contains("Generate", cut.Find(".vault-add-row__actions").TextContent, StringComparison.Ordinal);
        Assert.Contains("Add", cut.Find(".vault-add-row__actions").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/projects/1/vaults", "http://localhost/projects/1/vaults")]
    [InlineData("https://evil.example/x", "http://localhost/vaults")]
    [InlineData(null, "http://localhost/vaults")]
    public void DeletingAVault_ReturnsToThePageTheUserCameFrom(string? returnUrl, string expected)
    {
        MockVault();
        BunitTestHelper.UseImmediateDialogs(this);
        ((ImmediateDialogService)Services.GetRequiredService<OmniDialogService>()).ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, "api/vaults/1", HttpStatusCode.NoContent);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/vaults/1" + (returnUrl is null ? string.Empty : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));

        cut.Find(".omni-overflow-menu__trigger").Click();
        cut.FindAll(".omni-menu__item").First(item => item.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(expected, nav.Uri));
    }

    [Theory]
    [InlineData("/projects/1/variable-libraries", "http://localhost/projects/1/variable-libraries")]
    [InlineData("https://evil.example/x", "http://localhost/variable-libraries")]
    [InlineData(null, "http://localhost/variable-libraries")]
    public void DeletingALibrary_ReturnsToThePageTheUserCameFrom(string? returnUrl, string expected)
    {
        MockLibrary();
        BunitTestHelper.UseImmediateDialogs(this);
        ((ImmediateDialogService)Services.GetRequiredService<OmniDialogService>()).ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, "api/variable-libraries/1", HttpStatusCode.NoContent);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/variable-libraries/1" + (returnUrl is null ? string.Empty : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".omni-overflow-menu__trigger")));

        cut.Find(".omni-overflow-menu__trigger").Click();
        cut.FindAll(".omni-menu__item").First(item => item.TextContent.Contains("Delete", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(expected, nav.Uri));
    }

    [Fact]
    public void ALibraryPage_SaysWhatReadsIt()
    {
        MockLibrary();
        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Libraries = new() { [1] = [Pipeline(10, "deploy-prod")] }
        });

        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".resource-usage-line a[href='/pipelines/10']")));
    }
}
