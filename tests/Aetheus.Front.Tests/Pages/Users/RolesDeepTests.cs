// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Users;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Users;

public class RolesDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public RolesDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private static List<RoleDto> MakeRoles() =>
    [
        new() { Id = 1, Name = "Admin", Description = "Full access" },
        new() { Id = 2, Name = "Reader", Description = "Read-only" },
        new() { Id = 3, Name = "Contributor", Description = "Read+Write" },
        new() { Id = 4, Name = "CustomRole", Description = "Custom" }
    ];

    private void SetupRoles(List<RoleDto>? roles = null)
    {
        var items = roles ?? MakeRoles();
        _handler.SetJsonResponse("api/roles", new PaginatedResult<RoleDto>
        {
            Items = items,
            TotalCount = items.Count,
            Page = 1,
            PageSize = 25
        });
    }

    private async Task<IRenderedComponent<Roles>> RenderLoadedAsync()
    {
        var cut = Render<Roles>();
        var load = typeof(Roles).GetMethod("LoadDataAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)load.Invoke(cut.Instance,
            [new GridLoadArgs { Skip = 0, Top = 25 }])!);
        cut.Render();
        return cut;
    }

    [Fact]
    public async Task Renders_RoleList()
    {
        SetupRoles();
        var cut = await RenderLoadedAsync();
        cut.WaitForState(() => cut.Markup.Contains("Admin"));

        // The role list is loaded from api/roles and rendered.
        Assert.Contains("Admin", cut.Markup);
        Assert.Contains("Reader", cut.Markup);
    }

    [Fact]
    public async Task Loading_FalseAfterInit()
    {
        SetupRoles();
        var cut = await RenderLoadedAsync();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var loading = (bool)typeof(Roles).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    [Fact]
    public async Task OnCreate_OpensCreateRoleDialog()
    {
        SetupRoles();
        var cut = await RenderLoadedAsync();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // OnCreate opens the RoleCreateDialog via the OmniDialogService.
        var dialog = Services.GetRequiredService<OmniDialogService>();
        Type? openedDialog = null;
        dialog.OnOpen += (_, type, _, _) => openedDialog = type;

        var method = typeof(Roles).GetMethod("OnCreate", Priv)!;
        method.Invoke(cut.Instance, []);

        Assert.Equal("RoleCreateDialog", openedDialog?.Name);
    }

    [Theory]
    [InlineData("Admin", "RoleAdminDescription")]
    [InlineData("Reader", "RoleReaderDescription")]
    [InlineData("Contributor", "RoleContributorDescription")]
    public async Task LocalizeDescription_WellKnownRoles_ReturnsLocalized(string roleName, string expectedKey)
    {
        SetupRoles();
        var cut = await RenderLoadedAsync();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(Roles).GetMethod("LocalizeDescription", Priv)!;
        var role = new RoleDto { Id = 1, Name = roleName, Description = "raw" };
        var result = (string)method.Invoke(cut.Instance, [role])!;
        Assert.Equal(expectedKey, result);
    }

    [Fact]
    public async Task LocalizeDescription_CustomRole_ReturnsFallback()
    {
        SetupRoles();
        var cut = await RenderLoadedAsync();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(Roles).GetMethod("LocalizeDescription", Priv)!;
        var role = new RoleDto { Id = 4, Name = "CustomRole", Description = "custom desc" };
        var result = (string)method.Invoke(cut.Instance, [role])!;
        Assert.Equal("custom desc", result);
    }

    [Fact]
    public async Task LocalizeDescription_CustomRole_NullDescription_ReturnsEmpty()
    {
        SetupRoles();
        var cut = await RenderLoadedAsync();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(Roles).GetMethod("LocalizeDescription", Priv)!;
        var role = new RoleDto { Id = 5, Name = "NoDesc", Description = null! };
        var result = (string)method.Invoke(cut.Instance, [role])!;
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task OnClone_NavigatesToClonedRole()
    {
        SetupRoles();
        _handler.SetJsonResponse("api/roles/1/clone", new RoleDto { Id = 99, Name = "Admin (copy)", Description = "Full access" });
        var cut = await RenderLoadedAsync();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var role = new RoleDto { Id = 1, Name = "Admin", Description = "Full access" };
        var method = typeof(Roles).GetMethod("OnClone", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [role])!);

        // Clone POSTs to api/roles/1/clone, then lands on the cloned role's page (id 99).
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/roles/1/clone"));
        Assert.EndsWith("/admin/roles/99", nav.Uri);
    }

    [Fact]
    public void NonAdmin_DoesNotLoadRoles()
    {
        var ctx2 = new BunitContext();
        var h2 = BunitTestHelper.RegisterServices(ctx2, isAdmin: false);
        h2.SetJsonResponse("api/roles", new List<RoleDto>());
        ctx2.Render<Roles>();

        // The non-admin guard returns before LoadAsync fetches the role list.
        Assert.DoesNotContain(h2.Requests, r => r.Url.Contains("api/roles"));
    }
}
