// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using Aetheus.Front.Components.Shared;
using Aetheus.Front.Components.VariableLibraries;
using Aetheus.Front.Components.Vaults;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Recette R-64: "Used by" was asked once when a library or vault page opened, so a pipeline created
/// afterwards (the setup wizard creates one) left it saying "Unused" until a full reload. The pages follow the
/// pipelines through the entities hub and ask again; a failed ask keeps what was shown.</summary>
public sealed class ResourceUsageLiveRefreshTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private CapturingHubConnectionFactory Hubs => (CapturingHubConnectionFactory)Services.GetRequiredService<HubConnectionFactory>();

    public ResourceUsageLiveRefreshTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<HubConnectionFactory>(sp => new CapturingHubConnectionFactory(sp));
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "w6" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto());
    }

    private static ResourceUsageUserDto Pipeline(int id, string name) =>
        new() { Kind = ResourceUsageKind.Pipeline, Id = id, Name = name, ProjectId = 1 };

    private void MockLibrary()
    {
        _handler.SetJsonResponse("api/variable-libraries/1", new VariableLibraryDetailDto
        {
            Id = 1,
            Name = "w6.prod",
            ProjectId = 1,
            RowVersion = Guid.NewGuid(),
            EntryCount = 1
        });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/variable-libraries/1/entries",
            [new VariableEntryDto { Id = 1, Key = "DOMAIN", Value = "example.com" }]);
    }

    private void MockVault()
    {
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "w6-vault",
            ProjectId = 1,
            RowVersion = Guid.NewGuid(),
            Secrets = [new VaultSecretDto { Id = 10, Key = "DB_PASS" }]
        });
    }

    private async Task PipelineChangedAsync()
    {
        await Hubs.WhenCreatedAsync("entities");
        await HubHandlerInvoker.InvokeAsync(Hubs.Hub("entities"), "EntityChanged", ResourceType.Pipeline, 10, "Created");
    }

    [Fact]
    public async Task ALibraryPage_ShowsTheReaderOfAPipelineCreatedAfterItOpened()
    {
        MockLibrary();
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("ResourceUnused", cut.Find(".resource-usage-line").TextContent, StringComparison.Ordinal));

        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Libraries = new() { [1] = [Pipeline(10, "w6-deploy-prod")] }
        });
        await PipelineChangedAsync();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".resource-usage-line a[href='/pipelines/10']")));
        Assert.DoesNotContain("ResourceUnused", cut.Find(".resource-usage-line").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALibraryPage_KeepsItsReaders_WhenTheRefreshFails()
    {
        MockLibrary();
        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Libraries = new() { [1] = [Pipeline(10, "w6-deploy-prod")] }
        });
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".resource-usage-line a[href='/pipelines/10']")));

        _handler.SetResponse("api/pipelines/resource-usage", HttpStatusCode.InternalServerError);
        await PipelineChangedAsync();
        await _handler.WhenGetAsync("resource-usage", 2);

        Assert.NotNull(cut.Find(".resource-usage-line a[href='/pipelines/10']"));
        Assert.DoesNotContain("ResourceUnused", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALibraryPage_SaysNothingInsteadOfUnused_WhenTheFirstAskAndTheRefreshFail()
    {
        MockLibrary();
        _handler.SetResponse("api/pipelines/resource-usage", HttpStatusCode.InternalServerError);
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("w6.prod", cut.Markup));

        await PipelineChangedAsync();
        await _handler.WhenGetAsync("resource-usage", 2);

        Assert.DoesNotContain("ResourceUnused", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVaultPage_ShowsTheReaderOfAPipelineCreatedAfterItOpened()
    {
        MockVault();
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("ResourceUnused", cut.Find(".resource-usage-line").TextContent, StringComparison.Ordinal));

        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Vaults = new() { [1] = [Pipeline(10, "w6-deploy-prod")] }
        });
        await PipelineChangedAsync();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".resource-usage-line a[href='/pipelines/10']")));
    }

    [Fact]
    public async Task AVaultPage_KeepsItsReaders_WhenTheRefreshFails()
    {
        MockVault();
        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Vaults = new() { [1] = [Pipeline(10, "w6-deploy-prod")] }
        });
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".resource-usage-line a[href='/pipelines/10']")));

        _handler.SetResponse("api/pipelines/resource-usage", HttpStatusCode.InternalServerError);
        await PipelineChangedAsync();
        await _handler.WhenGetAsync("resource-usage", 2);

        Assert.NotNull(cut.Find(".resource-usage-line a[href='/pipelines/10']"));
        Assert.DoesNotContain("ResourceUnused", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALibraryList_ShowsTheReaderOfAPipelineCreatedAfterItOpened()
    {
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto>
        {
            Items = [new VariableLibraryDto { Id = 1, Name = "w6.prod", ProjectId = 1 }],
            TotalCount = 1
        });
        var cut = Render<VariableLibrariesList>();
        cut.WaitForAssertion(() => Assert.Contains("ResourceUnused", cut.Markup, StringComparison.Ordinal));

        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Libraries = new() { [1] = [Pipeline(10, "w6-deploy-prod")] }
        });
        await PipelineChangedAsync();

        cut.WaitForAssertion(() => Assert.Contains("w6-deploy-prod", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("ResourceUnused", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALibraryList_KeepsItsReaders_WhenTheRefreshFails()
    {
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto>
        {
            Items = [new VariableLibraryDto { Id = 1, Name = "w6.prod", ProjectId = 1 }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Libraries = new() { [1] = [Pipeline(10, "w6-deploy-prod")] }
        });
        var cut = Render<VariableLibrariesList>();
        cut.WaitForAssertion(() => Assert.Contains("w6-deploy-prod", cut.Markup, StringComparison.Ordinal));

        _handler.SetResponse("api/pipelines/resource-usage", HttpStatusCode.InternalServerError);
        await PipelineChangedAsync();
        await _handler.WhenGetAsync("resource-usage", 2);

        Assert.Contains("w6-deploy-prod", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceUnused", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVaultList_ShowsTheReaderOfAPipelineCreatedAfterItOpened()
    {
        _handler.SetJsonResponse("api/vaults", new PaginatedResult<VaultDto>
        {
            Items = [new VaultDto { Id = 1, Name = "w6-vault", ProjectId = 1 }],
            TotalCount = 1
        });
        var cut = Render<VaultsList>();
        cut.WaitForAssertion(() => Assert.Contains("ResourceUnused", cut.Markup, StringComparison.Ordinal));

        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Vaults = new() { [1] = [Pipeline(10, "w6-deploy-prod")] }
        });
        await PipelineChangedAsync();

        cut.WaitForAssertion(() => Assert.Contains("w6-deploy-prod", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("ResourceUnused", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVaultList_KeepsItsReaders_WhenTheRefreshFails()
    {
        _handler.SetJsonResponse("api/vaults", new PaginatedResult<VaultDto>
        {
            Items = [new VaultDto { Id = 1, Name = "w6-vault", ProjectId = 1 }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/pipelines/resource-usage", new ResourceUsageDto
        {
            Vaults = new() { [1] = [Pipeline(10, "w6-deploy-prod")] }
        });
        var cut = Render<VaultsList>();
        cut.WaitForAssertion(() => Assert.Contains("w6-deploy-prod", cut.Markup, StringComparison.Ordinal));

        _handler.SetResponse("api/pipelines/resource-usage", HttpStatusCode.InternalServerError);
        await PipelineChangedAsync();
        await _handler.WhenGetAsync("resource-usage", 2);

        Assert.Contains("w6-deploy-prod", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceUnused", cut.Markup, StringComparison.Ordinal);
    }
}
