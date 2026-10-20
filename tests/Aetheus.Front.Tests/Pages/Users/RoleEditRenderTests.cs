// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Users;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Users;

public class RoleEditRenderTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public RoleEditRenderTests() => _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);

    private static RoleDto BuildRole(int id = 1) => new()
    {
        Id = id,
        Name = "Developers",
        Description = "Dev team",
        Permissions =
        [
            new ResourcePermissionDto { ResourceType = ResourceType.Server, ResourceId = null, Permission = Permission.Read },
            new ResourcePermissionDto { ResourceType = ResourceType.Project, ResourceId = null, Permission = Permission.Write }
        ]
    };

    private void StubRole(int id)
    {
        _handler.SetJsonResponse($"api/roles/{id}", BuildRole(id));
    }

    [Fact]
    public void Renders_ExistingRole_LoadsName()
    {
        StubRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("ProgressBar"));

        // The stubbed role's name is loaded into instance state.
        var name = (string)typeof(RoleEdit).GetField("_name", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("Developers", name);
    }

    [Fact]
    public void BuildPermissionRows_AllResourceTypes_CreatesRows()
    {
        StubRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() =>
        {
            var rows = (List<RoleEdit.PermissionRowModel>?)typeof(RoleEdit)
                .GetField("_permissionRows", Priv)!.GetValue(cut.Instance);
            return rows?.Count > 0;
        });

        var permissionRows = (List<RoleEdit.PermissionRowModel>?)typeof(RoleEdit)
            .GetField("_permissionRows", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(permissionRows);
        Assert.Equal(Enum.GetValues<ResourceType>().Length, permissionRows!.Count);
    }

    [Fact]
    public void OnScopeChanged_ToNone_ClearsPermissions()
    {
        StubRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() =>
        {
            var r = (List<RoleEdit.PermissionRowModel>?)typeof(RoleEdit)
                .GetField("_permissionRows", Priv)!.GetValue(cut.Instance);
            return r?.Count > 0;
        });

        var row = new RoleEdit.PermissionRowModel
        {
            ResourceType = ResourceType.Server,
            Scope = "None",
            CanRead = true,
            CanWrite = true,
            CanAdmin = false
        };

        var method = typeof(RoleEdit).GetMethod("OnScopeChanged", Priv)!;
        method.Invoke(cut.Instance, [row]);

        Assert.False(row.CanRead);
        Assert.False(row.CanWrite);
        Assert.False(row.CanAdmin);
    }

    [Fact]
    public void OnPermissionToggled_AdminSet_SetsWriteAndRead()
    {
        StubRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));

        var row = new RoleEdit.PermissionRowModel
        {
            ResourceType = ResourceType.Server,
            Scope = "All",
            CanAdmin = true,
            CanWrite = false,
            CanRead = false
        };

        var method = typeof(RoleEdit).GetMethod("OnPermissionToggled", Priv)!;
        method.Invoke(cut.Instance, [row]);

        Assert.True(row.CanWrite);
        Assert.True(row.CanRead);
        Assert.Equal("All", row.Scope);
    }

    [Fact]
    public void OnPermissionToggled_WriteSet_SetsRead()
    {
        StubRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));

        var row = new RoleEdit.PermissionRowModel
        {
            ResourceType = ResourceType.Server,
            Scope = "All",
            CanAdmin = false,
            CanWrite = true,
            CanRead = false
        };

        var method = typeof(RoleEdit).GetMethod("OnPermissionToggled", Priv)!;
        method.Invoke(cut.Instance, [row]);

        Assert.True(row.CanRead);
    }

    [Fact]
    public void OnPermissionToggled_NoneSet_ScopeBecomesNone()
    {
        StubRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));

        var row = new RoleEdit.PermissionRowModel
        {
            ResourceType = ResourceType.Server,
            Scope = "All",
            CanAdmin = false,
            CanWrite = false,
            CanRead = false
        };

        var method = typeof(RoleEdit).GetMethod("OnPermissionToggled", Priv)!;
        method.Invoke(cut.Instance, [row]);

        Assert.Equal("None", row.Scope);
    }

    [Fact]
    public void OnCancel_NavigatesToRoles()
    {
        StubRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var method = typeof(RoleEdit).GetMethod("OnCancel", Priv)!;
        method.Invoke(cut.Instance, []);

        Assert.EndsWith("/admin/roles", nav.Uri);
    }

    [Fact]
    public void OnApplyBulk_NullBulkPermission_LeavesTheStateUnchanged()
    {
        StubRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() =>
        {
            var r = (List<RoleEdit.PermissionRowModel>?)typeof(RoleEdit)
                .GetField("_permissionRows", Priv)!.GetValue(cut.Instance);
            return r?.Count > 0;
        });

        typeof(RoleEdit).GetField("_bulkPermission", Priv)!.SetValue(cut.Instance, null);

        var rowsBefore = ((List<RoleEdit.PermissionRowModel>?)typeof(RoleEdit)
            .GetField("_permissionRows", Priv)!.GetValue(cut.Instance))
            ?.Select(r => r.CanAdmin).ToList();

        var method = typeof(RoleEdit).GetMethod("OnApplyBulk", Priv)!;
        method.Invoke(cut.Instance, []);

        var rowsAfter = ((List<RoleEdit.PermissionRowModel>?)typeof(RoleEdit)
            .GetField("_permissionRows", Priv)!.GetValue(cut.Instance))
            ?.Select(r => r.CanAdmin).ToList();

        Assert.Equal(rowsBefore, rowsAfter);
    }

    [Fact]
    public void OnApplyBulk_AdminPermission_SetsAllRowsToAdmin()
    {
        StubRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() =>
        {
            var r = (List<RoleEdit.PermissionRowModel>?)typeof(RoleEdit)
                .GetField("_permissionRows", Priv)!.GetValue(cut.Instance);
            return r?.Count > 0;
        });

        typeof(RoleEdit).GetField("_bulkPermission", Priv)!.SetValue(cut.Instance, (Permission?)Permission.Admin);

        var method = typeof(RoleEdit).GetMethod("OnApplyBulk", Priv)!;
        method.Invoke(cut.Instance, []);

        var rows = (List<RoleEdit.PermissionRowModel>?)typeof(RoleEdit)
            .GetField("_permissionRows", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(rows);
        Assert.All(rows!, r =>
        {
            Assert.True(r.CanAdmin);
            Assert.True(r.CanWrite);
            Assert.True(r.CanRead);
            Assert.Equal("All", r.Scope);
        });
    }
}
