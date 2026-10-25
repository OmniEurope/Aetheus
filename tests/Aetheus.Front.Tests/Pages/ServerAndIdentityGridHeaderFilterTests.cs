// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Components.Users;
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using OrganizationsPage = Aetheus.Front.Components.Organizations.Organizations;
using UsersPage = Aetheus.Front.Components.Users.Users;

namespace Aetheus.Front.Tests.Pages;

/// <summary> A header filter of the server detail sections and of the identity pages is sent to the
/// API as a column filter (<c>Filters[i].Field</c>...), which the endpoint applies before counting.
/// Each test sets the filter the way the column header does and reads the request it causes.
/// </summary>
public sealed class ServerAndIdentityGridHeaderFilterTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerAndIdentityGridHeaderFilterTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private static bool Sends((string Method, string Url) request, string path, string field, string value)
    {
        var url = Uri.UnescapeDataString(request.Url);
        return url.Contains(path, StringComparison.Ordinal)
            && url.Contains($"Filters[0].Field={field}", StringComparison.Ordinal)
            && url.Contains($"Filters[0].Value={value}", StringComparison.Ordinal);
    }

    private static async Task FilterAsync<TComponent, TItem>(IRenderedComponent<TComponent> cut, string key, string value)
        where TComponent : class, Microsoft.AspNetCore.Components.IComponent
        where TItem : notnull
    {
        AetheusDataGrid<TItem>? grid = null;
        cut.WaitForAssertion(() => grid = cut.FindComponent<AetheusDataGrid<TItem>>().Instance);
        await cut.InvokeAsync(() => grid!.Grid!.SetFiltersAsync(new Dictionary<string, string?> { [key] = value }));
    }

    [Fact]
    public async Task RetiredServers_NameFilter_ReachesTheApi()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers/retired", new PaginatedResult<RetiredServerDto>());
        var cut = Render<RetiredServersDialog>();

        await FilterAsync<RetiredServersDialog, RetiredServerDto>(cut, "Name", "vps");

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => Sends(r, "api/servers/retired", "Name", "vps")));
    }

    [Fact]
    public async Task ModuleLinks_TypeFilter_TravelsInThePageRequestBody()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Post, "api/servers/1/module-links/resource-page", new List<LinkedResourceDto>());
        var cut = Render<ModuleLinksTab>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.SourceType, ModuleLinkType.Docker)
            .Add(x => x.Resources, ["nginx"]));

        await FilterAsync<ModuleLinksTab, LinkedResourceDto>(cut, "Type", "Apache");

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request =>
            request.Method == "POST"
            && request.Body is { } body
            && body.Contains("\"field\":\"Type\"", StringComparison.OrdinalIgnoreCase)
            && body.Contains("\"value\":\"Apache\"", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ServerApps_StatusFilter_ReachesTheApi_AndSourcesComeFromTheFilterValuesEndpoint()
    {
        _handler.SetPaginatedJsonResponse("api/servers/1/apps", new List<ServerAppDto>());
        _handler.SetJsonResponse("api/servers/1/apps/filter-values", new ServerAppFilterValuesDto { Sources = ["docker", "systemd"] });
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 1));

        await FilterAsync<ServerAppsSection, ServerAppDto>(cut, "Status", "Running");

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => Sends(r, "api/servers/1/apps", "Status", "Running")));
        Assert.Contains(_handler.Requests, r => r.Url.EndsWith("api/servers/1/apps/filter-values", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mail_DkimSelectorFilter_ReachesTheDomainsApi()
    {
        var server = Servers.ServerTestData.MakeMailServer(true, true, true);
        _handler.SetJsonResponse($"api/servers/{server.Id}/mail/domains", new PaginatedResult<MailDomainDto>());
        _handler.SetJsonResponse($"api/servers/{server.Id}/mail/accounts", new PaginatedResult<MailAccountDto>());
        _handler.SetJsonResponse($"api/servers/{server.Id}/mail/aliases", new PaginatedResult<MailAliasDto>());
        _handler.SetJsonResponse($"api/servers/{server.Id}/mail/diagnostics", new MailDiagnosticsDto());
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>(Services).NavigateTo($"/servers/{server.Id}/mail?tab=domains");
        var cut = Render<ServerMailSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));

        await FilterAsync<ServerMailSection, MailDomainDto>(cut, "DkimSelector", "mail");

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r =>
            Sends(r, $"api/servers/{server.Id}/mail/domains", "DkimSelector", "mail")));
    }

    [Fact]
    public async Task Portsentry_ProtocolFilter_ReachesTheBlockedApi()
    {
        _handler.SetJsonResponse("api/servers/50/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>());
        _handler.SetJsonResponse("api/servers/50/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>());
        _handler.SetJsonResponse("api/servers/50/portsentry/filter-values", new PortsentryFilterValuesDto { Protocols = ["tcp", "udp"] });
        var cut = Render<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Ps, new PortsentryDataDto { IsInstalled = true, IsRunning = true }));

        await FilterAsync<ServerPortsentrySection, PortsentryBlockedIpDto>(cut, "Protocol", "tcp");

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => Sends(r, "api/servers/50/portsentry/blocked", "Protocol", "tcp")));
    }

    [Fact]
    public async Task ServerProjects_StatusFilter_ReachesTheApi()
    {
        _handler.SetJsonResponse("api/servers/1/projects", new PaginatedResult<ProjectDto>());
        var cut = Render<ServerProjectsSection>(p => p.Add(x => x.ServerId, 1));

        await FilterAsync<ServerProjectsSection, ProjectDto>(cut, "Status", "Archived");

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => Sends(r, "api/servers/1/projects", "Status", "Archived")));
    }

    [Fact]
    public async Task Users_RolesFilter_ReachesTheApi_WithTheRoleNamesAsCandidates()
    {
        _handler.SetJsonResponse("api/users/roles", new List<string> { "Admin", "Reader" });
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>());
        var cut = Render<UsersPage>();

        await FilterAsync<UsersPage, UserDto>(cut, "Roles", "Admin");

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => Sends(r, "api/users", "Roles", "Admin")));
        Assert.Contains(_handler.Requests, r => r.Url.EndsWith("api/users/roles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Roles_NameFilter_ReachesTheApi()
    {
        _handler.SetJsonResponse("api/roles", new PaginatedResult<RoleDto>());
        var cut = Render<Roles>();

        await FilterAsync<Roles, RoleDto>(cut, "Name", "adm");

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => Sends(r, "api/roles", "Name", "adm")));
    }

    [Fact]
    public async Task RoleEdit_UsersGridStatusFilter_ReachesTheRoleUsersApi()
    {
        _handler.SetJsonResponse("api/roles/5", new RoleDto { Id = 5, Name = "Ops" });
        _handler.SetJsonResponse("api/roles/5/users", new PaginatedResult<RoleUserDto>());
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 5));
        var load = typeof(RoleEdit).GetMethod("LoadRoleUsersAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // The users tab is lazy; its LoadData receives the header filter as the grid hands it over.
        await cut.InvokeAsync(async () => await (Task)load.Invoke(cut.Instance,
        [
            new GridLoadArgs
            {
                Skip = 0,
                Top = 25,
                Filters = [new GridFilterDescriptor("IsActive", bool.FalseString, OmniDataGridFilterOperator.Equals)]
            }
        ])!);

        Assert.Contains(_handler.Requests, r => Sends(r, "api/roles/5/users", "IsActive", bool.FalseString));
    }

    [Fact]
    public async Task Organizations_SlugFilter_ReachesTheApi()
    {
        _handler.SetJsonResponse("api/organizations", new PaginatedResult<OrganizationDto>());
        var cut = Render<OrganizationsPage>();

        await FilterAsync<OrganizationsPage, OrganizationDto>(cut, "Slug", "acme");

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => Sends(r, "api/organizations", "Slug", "acme")));
    }

    [Fact]
    public void Users_RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/users/roles", new List<string> { "Admin" });
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>
        {
            Items = [new UserDto { Id = 3, Username = "bob", Email = "bob@example.org", IsActive = true, Roles = ["Admin"] }],
            TotalCount = 1
        });

        var cut = Render<UsersPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        // The user link and the edit button.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 2);
    }
}
