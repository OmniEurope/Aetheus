// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Settings;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>
/// Owner decision 2026-10-28: an organization's administrator gets the Administration section's pages its
/// permissions open (users, organizations, roles, mail templates) and nothing else; the organization switcher
/// is shown only when there is a choice to make. The backend decides (OrganizationAdminSectionIsolation
/// integration tests); these tests prove what the UI offers.
/// </summary>
public class OrganizationAdministrationSectionTests : BunitContext
{
    private static readonly string[] AllOrganizationKeys =
    [
        PermissionKeys.OrganizationMembersManage,
        PermissionKeys.OrganizationRolesManage,
        PermissionKeys.OrganizationMailTemplatesManage
    ];

    private BunitTestHelper.TestHandler Register(IReadOnlyCollection<string> organizationKeys)
    {
        // NavMenu needs the alert service; it is built before RegisterServices freezes the container.
        var authProvider = new AuthStateProvider(Substitute.For<IJSRuntime>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthStateProvider>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://test:5301" })
            .Build();
        var hubFactory = new HubConnectionFactory(config, authProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthDelegatingHandler>.Instance);
        Services.AddSingleton(new AlertNotificationService(authProvider, hubFactory));
        return BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false, extraPermissionKeys: organizationKeys);
    }

    private IRenderedComponent<NavMenu> RenderMenuOnAdministration()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("admin");
        return Render<NavMenu>();
    }

    [Fact]
    public void AnOrganizationAdministrator_SeesTheFourPagesOfTheSection_AndNoOtherAdministrationPage()
    {
        Register(AllOrganizationKeys);

        var menu = RenderMenuOnAdministration();

        Assert.NotEmpty(menu.FindAll("a[href='admin']"));
        foreach (var page in new[] { "admin/users", "admin/organizations", "admin/roles", "admin/mail-templates" })
            Assert.NotEmpty(menu.FindAll($"a[href='{page}']"));
        foreach (var page in new[] { "admin/audit", "admin/settings", "admin/plugins", "admin/notifications", "admin/system-logs" })
            Assert.Empty(menu.FindAll($"a[href='{page}']"));
    }

    [Fact]
    public void TheMenu_OffersOnlyThePagesTheKeysHeldOpen()
    {
        Register([PermissionKeys.OrganizationRolesManage]);

        var menu = RenderMenuOnAdministration();

        Assert.NotEmpty(menu.FindAll("a[href='admin/roles']"));
        Assert.Empty(menu.FindAll("a[href='admin/users']"));
        Assert.Empty(menu.FindAll("a[href='admin/mail-templates']"));
    }

    [Fact]
    public void AUserAdministeringNoOrganization_HasNoAdministrationEntry()
    {
        Register([]);

        var menu = RenderMenuOnAdministration();

        Assert.Empty(menu.FindAll("a[href='admin']"));
        Assert.Empty(menu.FindAll("a[href='admin/users']"));
    }

    [Fact]
    public void TheHub_ShowsTheOrganizationAdministratorItsTilesOnly()
    {
        Register([PermissionKeys.OrganizationMembersManage, PermissionKeys.OrganizationMailTemplatesManage]);

        var hub = Render<Administration>();

        var tiles = hub.FindAll("a[href^='/admin/']").Select(link => link.GetAttribute("href")).ToList();
        Assert.Equal(["/admin/users", "/admin/organizations", "/admin/mail-templates"], tiles);
    }

    [Theory]
    [InlineData("admin/users", PermissionKeys.OrganizationMembersManage)]
    [InlineData("admin/organizations", PermissionKeys.OrganizationMembersManage)]
    [InlineData("admin/organizations/7", PermissionKeys.OrganizationMembersManage)]
    [InlineData("admin/roles", PermissionKeys.OrganizationRolesManage)]
    [InlineData("admin/roles/12", PermissionKeys.OrganizationRolesManage)]
    [InlineData("admin/roles/new", PermissionKeys.OrganizationRolesManage)]
    [InlineData("admin/mail-templates", PermissionKeys.OrganizationMailTemplatesManage)]
    [InlineData("admin/organizations/new", null)]
    [InlineData("admin/organizations/7/edit", null)]
    // R-10 (d): an organization's administrator creates an account in its organizations.
    [InlineData("admin/users/new", PermissionKeys.OrganizationMembersManage)]
    [InlineData("admin/users/3", null)]
    [InlineData("admin/audit", null)]
    [InlineData("admin/settings", null)]
    public void EachRouteOfTheSection_NeedsItsOwnKey(string path, string? key)
    {
        Assert.Equal(key, OrganizationAdministration.KeyFor(path));
    }

    [Fact]
    public void TheRoutePolicy_LetsTheOrganizationAdministratorIntoItsPagesOnly()
    {
        Register([PermissionKeys.OrganizationRolesManage]);
        var permissions = Services.GetRequiredService<PermissionService>();

        Assert.True(RouteAccessPolicy.CanAccess("/admin", true, false, permissions));
        Assert.True(RouteAccessPolicy.CanAccess("/admin/roles/4", true, false, permissions));
        Assert.False(RouteAccessPolicy.CanAccess("/admin/users", true, false, permissions));
        Assert.False(RouteAccessPolicy.CanAccess("/admin/settings", true, false, permissions));
        Assert.False(RouteAccessPolicy.CanAccess("/users/3", true, false, permissions));
    }

    [Fact]
    public void TheSwitcher_IsHidden_WhenTheUserAdministersOneOrganization()
    {
        var handler = Register(AllOrganizationKeys);
        handler.SetJsonResponse("api/organizations/administered", new List<AdministeredOrganizationDto> { new(1, "Acme", AllOrganizationKeys) });

        var switcher = Render<AdministrationOrganizationSwitcher>(parameters => parameters.Add(component => component.PermissionKey, PermissionKeys.OrganizationMembersManage));

        switcher.WaitForAssertion(() => Assert.Empty(switcher.FindAll("#admin-scope-organization")));
    }

    [Fact]
    public void TheSwitcher_ListsTheOrganizations_AndItsChoiceFiltersThePages()
    {
        var handler = Register(AllOrganizationKeys);
        handler.SetJsonResponse("api/organizations/administered",
            new List<AdministeredOrganizationDto> { new(1, "Acme", AllOrganizationKeys), new(2, "Globex", AllOrganizationKeys) });
        var changed = 0;

        var switcher = Render<AdministrationOrganizationSwitcher>(parameters => parameters
            .Add(component => component.PermissionKey, PermissionKeys.OrganizationMembersManage)
            .Add(component => component.Changed, () => changed++));

        switcher.WaitForAssertion(() => Assert.NotEmpty(switcher.FindAll("#admin-scope-organization")));
        Assert.Equal(["AdminScopeAllOrganizations", "Acme", "Globex"],
            switcher.Instance.Options.Select(option => option.Text));

        var dropDown = switcher.FindComponent<OmniDropDown<int>>();
        switcher.InvokeAsync(() => dropDown.Instance.ValueChanged.InvokeAsync(2));

        Assert.Equal(2, Services.GetRequiredService<AdministrationScopeService>().SelectedOrganizationId);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void TheSwitcher_ListsOnlyTheOrganizationsHoldingTheKeyOfItsPage_AndAChoiceOfAnotherPageFallsBackToAll()
    {
        var handler = Register(AllOrganizationKeys);
        handler.SetJsonResponse("api/organizations/administered", new List<AdministeredOrganizationDto>
        {
            new(1, "Acme", [PermissionKeys.OrganizationMembersManage, PermissionKeys.OrganizationRolesManage]),
            new(2, "Globex", [PermissionKeys.OrganizationRolesManage]),
            new(3, "Initech", [PermissionKeys.OrganizationMembersManage])
        });

        var roles = Render<AdministrationOrganizationSwitcher>(parameters => parameters
            .Add(component => component.PermissionKey, PermissionKeys.OrganizationRolesManage));
        roles.WaitForAssertion(() => Assert.NotEmpty(roles.FindAll("#admin-scope-organization")));
        Assert.Equal(["AdminScopeAllOrganizations", "Acme", "Globex"], roles.Instance.Options.Select(option => option.Text));

        // Globex is chosen on the Roles page; the Users page does not hold it there: no 403 is ever asked for.
        var scope = Services.GetRequiredService<AdministrationScopeService>();
        scope.Select(2);
        Assert.Equal(2, scope.SelectedFor(PermissionKeys.OrganizationRolesManage));
        Assert.Null(scope.SelectedFor(PermissionKeys.OrganizationMembersManage));

        var users = Render<AdministrationOrganizationSwitcher>(parameters => parameters
            .Add(component => component.PermissionKey, PermissionKeys.OrganizationMembersManage));
        users.WaitForAssertion(() => Assert.NotEmpty(users.FindAll("#admin-scope-organization")));
        Assert.Equal(["AdminScopeAllOrganizations", "Acme", "Initech"], users.Instance.Options.Select(option => option.Text));
        Assert.Equal(0, users.Instance.CurrentValue);
    }

    [Fact]
    public async Task TheScope_RefusesAnOrganizationTheUserDoesNotAdminister()
    {
        var handler = Register(AllOrganizationKeys);
        handler.SetJsonResponse("api/organizations/administered", new List<AdministeredOrganizationDto> { new(1, "Acme", AllOrganizationKeys) });
        var scope = Services.GetRequiredService<AdministrationScopeService>();
        await scope.EnsureLoadedAsync(Xunit.TestContext.Current.CancellationToken);

        scope.Select(99);

        Assert.Null(scope.SelectedOrganizationId);
        Assert.Equal(1, scope.SelectedOrFirstFor(PermissionKeys.OrganizationMembersManage));
    }
}
