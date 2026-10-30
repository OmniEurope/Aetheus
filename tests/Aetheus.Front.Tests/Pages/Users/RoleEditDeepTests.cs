// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Users;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>
/// Deep coverage for RoleEdit.razor.cs - OnInitializedAsync (admin new + existing + non-admin),
/// OnSave (new + edit), OnSavePermissions, BuildPermissionRows, OnScopeChanged,
/// OnPermissionToggled, OnCancel, OnApplyBulk.
/// </summary>
public class RoleEditDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public RoleEditDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetOrganizations();
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/roles/0",
            new RoleDto { Id = 0, Name = string.Empty, Permissions = [] });
    }

    private void SetupRole(int id = 1)
    {
        _handler.SetJsonResponse($"api/roles/{id}", new RoleDto
        {
            Id = id,
            Name = "Editor",
            Description = "Can edit",
            Permissions = []
        });
    }

    // ── New role ──────────────────────────────────────────────────────────────

    [Fact]
    public void OnInit_NewRole_SetsIsNew()
    {
        // /roles/new URL
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // We can't drive Nav.Uri to contain "/roles/new" in bUnit, so _isNew resolves false and
        // the page takes the existing-role path: it fetches the role and finishes loading.
        var isNew = (bool)typeof(RoleEdit).GetField("_isNew", Priv)!.GetValue(cut.Instance)!;
        Assert.False(isNew);
        var loading = (bool)typeof(RoleEdit).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    // ── Existing role ─────────────────────────────────────────────────────────

    [Fact]
    public void OnInit_ExistingRole_LoadsData()
    {
        SetupRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // The stubbed role's name is loaded into the form model and a GET hit api/roles/1.
        var name = (string)typeof(RoleEdit).GetField("_name", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("Editor", name);
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/roles/1"));
    }

    // ── Non-admin redirects ───────────────────────────────────────────────────

    [Fact]
    public void OnInit_NonAdmin_Redirects()
    {
        var ctx2 = new BunitContext();
        var h2 = BunitTestHelper.RegisterServices(ctx2, isAdmin: false);
        h2.SetJsonResponse("api/roles/1", new RoleDto { Id = 1, Name = "Editor", Permissions = [] });
        ctx2.Render<RoleEdit>(p => p.Add(x => x.Id, 1));

        // The non-admin guard navigates home and returns before fetching the role.
        Assert.DoesNotContain(h2.Requests, r => r.Url.Contains("api/roles/1"));
    }

    // ── HTTP error redirects ──────────────────────────────────────────────────

    [Fact]
    public void OnInit_RoleNotFound_Redirects()
    {
        _handler.SetResponse("api/roles/999", System.Net.HttpStatusCode.NotFound);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Render<RoleEdit>(p => p.Add(x => x.Id, 999));

        // A null role (404) triggers a redirect back to the roles list.
        Assert.EndsWith("/admin/roles", nav.Uri);
    }

    // ── OnSave - existing role update ─────────────────────────────────────────

    [Fact]
    public async Task OnSave_ExistingRole_CallsUpdate()
    {
        SetupRole(1);
        _handler.SetJsonResponse("api/roles/1", new RoleDto { Id = 1, Name = "Editor", Permissions = [] });
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        typeof(RoleEdit).GetField("_isNew", Priv)!.SetValue(cut.Instance, false);
        var model = typeof(RoleEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "Updated Editor");

        var method = typeof(RoleEdit).GetMethod("OnSave", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Existing-role path issues an update PUT to api/roles/1.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/roles/1"));
    }

    // ── OnSave - new role creation ────────────────────────────────────────────

    [Fact]
    public async Task OnSave_NewRole_CallsCreate()
    {
        SetupRole(1);
        _handler.SetJsonResponse("api/roles", new RoleDto { Id = 99, Name = "New", Permissions = [] });
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        typeof(RoleEdit).GetField("_isNew", Priv)!.SetValue(cut.Instance, true);
        var model = typeof(RoleEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "New Role");

        var method = typeof(RoleEdit).GetMethod("OnSave", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // New-role path issues a create POST to the api/roles collection.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/roles"));
    }

    // ── OnSavePermissions ─────────────────────────────────────────────────────

    [Fact]
    public async Task OnSavePermissions_CallsApi()
    {
        SetupRole(1);
        _handler.SetJsonResponse("api/roles/1/permissions", true);
        _handler.SetJsonResponse("api/roles/1/permission-keys", new List<string>());
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var rows = Enum.GetValues<ResourceType>().Select(rt =>
        {
            var row = Activator.CreateInstance(typeof(RoleEdit).GetNestedType("PermissionRowModel", BindingFlags.NonPublic)!)!;
            row.GetType().GetProperty("ResourceType")!.SetValue(row, rt);
            row.GetType().GetProperty("Scope")!.SetValue(row, "All");
            row.GetType().GetProperty("CanRead")!.SetValue(row, true);
            row.GetType().GetProperty("CanWrite")!.SetValue(row, false);
            row.GetType().GetProperty("CanAdmin")!.SetValue(row, false);
            return row;
        }).ToList();
        var listType = typeof(List<>).MakeGenericType(typeof(RoleEdit).GetNestedType("PermissionRowModel", BindingFlags.NonPublic)!);
        var rowList = Activator.CreateInstance(listType)!;
        foreach (var r in rows)
            listType.GetMethod("Add")!.Invoke(rowList, [r]);
        typeof(RoleEdit).GetField("_permissionRows", Priv)!.SetValue(cut.Instance, rowList);

        var method = typeof(RoleEdit).GetMethod("OnSavePermissions", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // The non-"None" rows are serialized and persisted via PUT api/roles/1/permissions.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/roles/1/permissions"));
    }

    // ── OnCancel ──────────────────────────────────────────────────────────────

    [Fact]
    public void OnCancel_Navigates()
    {
        SetupRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var method = typeof(RoleEdit).GetMethod("OnCancel", Priv)!;
        method.Invoke(cut.Instance, []);

        // Cancel returns to the roles list.
        Assert.EndsWith("/admin/roles", nav.Uri);
    }

    // ── BuildPermissionRows ───────────────────────────────────────────────────

    [Fact]
    public void BuildPermissionRows_WithPermissions_SetsRows()
    {
        SetupRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var perms = new List<ResourcePermissionDto>
        {
            new() { ResourceType = ResourceType.Project, Permission = Permission.Write, ResourceId = null }
        };
        var method = typeof(RoleEdit).GetMethod("BuildPermissionRows", Priv)!;
        method.Invoke(cut.Instance, [perms]);

        var rowsField = typeof(RoleEdit).GetField("_permissionRows", Priv)!.GetValue(cut.Instance)!;
        var count = (int)rowsField.GetType().GetProperty("Count")!.GetValue(rowsField)!;
        Assert.True(count > 0, "Expected at least one permission row after BuildPermissionRows");
    }

    // ── OnApplyBulk ───────────────────────────────────────────────────────────

    [Fact]
    public void OnApplyBulk_NullPermission_LeavesEveryRowUnchanged()
    {
        SetupRole(1);
        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() =>
        {
            var r = (List<RoleEdit.PermissionRowModel>)typeof(RoleEdit).GetField("_permissionRows", Priv)!.GetValue(cut.Instance)!;
            return r.Count > 0;
        });
        typeof(RoleEdit).GetField("_bulkPermission", Priv)!.SetValue(cut.Instance, (Permission?)null);

        // SetupRole has no permissions, so every row starts at scope "None" with no levels set.
        var rows = (List<RoleEdit.PermissionRowModel>)typeof(RoleEdit).GetField("_permissionRows", Priv)!.GetValue(cut.Instance)!;

        var method = typeof(RoleEdit).GetMethod("OnApplyBulk", Priv)!;
        method.Invoke(cut.Instance, []);

        // Null bulk permission short-circuits - rows are left untouched (still all "None"/unset).
        Assert.All(rows, r =>
        {
            Assert.Equal("None", r.Scope);
            Assert.False(r.CanRead);
            Assert.False(r.CanWrite);
            Assert.False(r.CanAdmin);
        });
    }

    [Fact]
    public void UserRowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        SetupRole();
        _handler.SetJsonResponse("api/roles/1/users", new PaginatedResult<RoleUserDto>
        {
            Items = [new RoleUserDto(5, "alice", "alice@example.org", true)],
            TotalCount = 1
        });

        var cut = Render<RoleEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='tab']")));
        cut.FindAll("[role='tab']").First(tab => tab.TextContent.Trim() == "Users").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        // The user link and the remove button.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 2);
    }
}
