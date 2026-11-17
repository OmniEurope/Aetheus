// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Logs;
using Aetheus.Front.Components.Organizations;
using Aetheus.Front.Components.PackageFeeds;
using Aetheus.Front.Components.PackageRegistry;
using Aetheus.Shared.Components.Organizations;
using Aetheus.Shared.Components.PackageRegistry;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OrganizationsPage = Aetheus.Front.Components.Organizations.Organizations;

namespace Aetheus.Front.Tests.Pages;

/// <summary>The admin hub is a refresh channel: a page draws from its data load and never waits for the
/// realtime negotiation (up to the start timeout under load). Every test renders a page over a hub
/// whose start never answers and requires the loaded data on screen well before that timeout.</summary>
public sealed class AdminHubStartDoesNotHoldThePageTests : BunitContext
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);
    private readonly BunitTestHelper.TestHandler _handler;
    private BunitTestHelper.BlockingHubConnectionFactory? _hubs;

    public AdminHubStartDoesNotHoldThePageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
        Services.AddSingleton<HubConnectionFactory>(sp => _hubs = new BunitTestHelper.BlockingHubConnectionFactory(
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            sp.GetRequiredService<AuthStateProvider>()));
    }

    /// <summary>Runs the page's own initialization once more and requires it to complete within the limit
    /// while the hub never answers: Blazor draws the page, and runs OnParametersSet, only when it completes.</summary>
    private static async Task AssertInitializationEndsAsync<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : Microsoft.AspNetCore.Components.IComponent, IInitializationProbe
    {
        var task = await cut.InvokeAsync(() => (object)cut.Instance.InitializeAgain());

        var finished = await Task.WhenAny((Task)task, Task.Delay(Limit));

        Assert.Same(task, finished);
        await (Task)task;
    }

    /// <summary>Exposes the page's protected OnInitializedAsync to the test, without reflection.</summary>
    public interface IInitializationProbe
    {
        Task InitializeAgain();
    }

    public sealed class OrganizationsProbe : OrganizationsPage, IInitializationProbe
    {
        public Task InitializeAgain() => OnInitializedAsync();
    }

    public sealed class OrganizationDetailProbe : OrganizationDetail, IInitializationProbe
    {
        public Task InitializeAgain() => OnInitializedAsync();
    }

    public sealed class PluginManagementProbe : PluginManagement, IInitializationProbe
    {
        public Task InitializeAgain() => OnInitializedAsync();
    }

    public sealed class AuditLogsProbe : AuditLogs, IInitializationProbe
    {
        public Task InitializeAgain() => OnInitializedAsync();
    }

    public sealed class SystemLogsProbe : SystemLogs, IInitializationProbe
    {
        public Task InitializeAgain() => OnInitializedAsync();
    }

    public sealed class PackageFeedPackagesDialogProbe : PackageFeedPackagesDialog, IInitializationProbe
    {
        public Task InitializeAgain() => OnInitializedAsync();
    }

    public sealed class PackageRegistryPackageDialogProbe : PackageRegistryPackageDialog, IInitializationProbe
    {
        public Task InitializeAgain() => OnInitializedAsync();
    }

    private void AssertHubWasAskedFor() => Assert.True(_hubs?.Created >= 1, "the page subscribes to the admin hub");

    [Fact]
    public async Task Organizations_ShowTheirRows()
    {
        _handler.SetJsonResponse("api/organizations", new PaginatedResult<OrganizationDto>
        {
            Items = [new OrganizationDto(1, "Acme", "acme", "Acme Corp", 5, 3, DateTime.UtcNow, DateTime.UtcNow)],
            TotalCount = 1
        });

        var cut = Render<OrganizationsProbe>();

        cut.WaitForAssertion(() => Assert.Contains("Acme", cut.Markup), Limit);
        await AssertInitializationEndsAsync(cut);
        AssertHubWasAskedFor();
    }

    [Fact]
    public async Task OrganizationDetail_ShowsTheOrganization()
    {
        _handler.SetJsonResponse("api/organizations/1", new OrganizationDetailDto(
            1, "Acme Corp", "acme", "A test org", DateTime.UtcNow, DateTime.UtcNow,
            [new OrganizationMemberDto(1, 10, "alice", "a@b.c", OrganizationRole.Owner, DateTime.UtcNow)],
            [new OrganizationProjectDto(1, "Project 1", ProjectStatus.Active)]));
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>
        {
            Items = [new UserDto { Id = 10, Username = "alice" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Project 1" }],
            TotalCount = 1
        });

        var cut = Render<OrganizationDetailProbe>(p => p.Add(x => x.Id, 1));

        cut.WaitForAssertion(() => Assert.Contains("Acme Corp", cut.Markup), Limit);
        await AssertInitializationEndsAsync(cut);
        AssertHubWasAskedFor();
    }

    [Fact]
    public async Task PluginManagement_ShowsItsRows()
    {
        _handler.SetPaginatedJsonResponse("api/plugins", new List<PluginRegistrationDto>());

        var cut = Render<PluginManagementProbe>();

        cut.WaitForAssertion(() => Assert.Contains("NoRecords", cut.Markup), Limit);
        await AssertInitializationEndsAsync(cut);
        AssertHubWasAskedFor();
    }

    [Fact]
    public async Task AuditLogs_ShowTheirRows()
    {
        _handler.SetJsonResponse("api/audit/actions", new List<string> { "Created" });
        _handler.SetJsonResponse("api/audit/entity-types", new List<string> { "Server" });
        _handler.SetJsonResponse("api/audit", new PaginatedResult<AuditLogDto>
        {
            Items = [new AuditLogDto { Id = 1, Action = "Created", EntityType = "Server", Username = "admin", Timestamp = DateTime.UtcNow }],
            TotalCount = 1
        });

        var cut = Render<AuditLogsProbe>();

        cut.WaitForAssertion(() => Assert.Contains("admin", cut.Markup), Limit);
        await AssertInitializationEndsAsync(cut);
        AssertHubWasAskedFor();
    }

    [Fact]
    public async Task SystemLogs_ShowTheirTabs()
    {
        _handler.SetJsonResponse("api/system-logs/files", new List<SystemLogFileDto> { new("app-20260424.log", 102_400, DateTime.UtcNow) });
        _handler.SetJsonResponse("api/system-logs/entries", new PaginatedResult<SystemLogEntryDto> { TotalCount = 0, Items = [] });

        var cut = Render<SystemLogsProbe>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".omni-tabs.system-logs-tabs")), Limit);
        await AssertInitializationEndsAsync(cut);
        AssertHubWasAskedFor();
    }

    [Fact]
    public async Task PackageFeedPackagesDialog_ShowsItsPackages()
    {
        _handler.SetJsonResponse("api/package-feeds/3", new PackageFeedDetailDto
        {
            Id = 3,
            Name = "corp-nuget",
            Packages = [new PackageEntryDto { Id = 7, PackageFeedId = 3, Name = "Serilog" }]
        });

        var cut = Render<PackageFeedPackagesDialogProbe>(p => p.Add(x => x.FeedId, 3));

        cut.WaitForAssertion(() => Assert.Contains("Serilog", cut.Markup), Limit);
        await AssertInitializationEndsAsync(cut);
        AssertHubWasAskedFor();
    }

    [Fact]
    public async Task PackageRegistryPackageDialog_ShowsItsVersions()
    {
        _handler.SetJsonResponse("api/package-registry/4", new PackageRegistryPackageDetailDto
        {
            Id = 4,
            Name = "corp-tools",
            Versions = [new PackageRegistryVersionDto { Id = 9, Version = "2.7.1", PublishedBy = "ci" }]
        });

        var cut = Render<PackageRegistryPackageDialogProbe>(p => p.Add(x => x.PackageId, 4));

        cut.WaitForAssertion(() => Assert.Contains("2.7.1", cut.Markup), Limit);
        await AssertInitializationEndsAsync(cut);
        AssertHubWasAskedFor();
    }
}
