// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Users;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>R-11: the role's "Access" tab ticks named permissions by area, marks a built-in role's defaults,
/// shows the site Admin read-only, and saves the ticked keys in catalogue order.</summary>
public class RolePermissionKeysEditorTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public RolePermissionKeysEditorTests() => _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);

    private IRenderedComponent<RolePermissionKeysEditor> RenderFor(string roleName, params string[] keys) =>
        Render<RolePermissionKeysEditor>(p => p
            .Add(x => x.RoleId, 7)
            .Add(x => x.RoleName, roleName)
            .Add(x => x.Keys, keys));

    [Fact]
    public void EveryAreaOfTheCatalogue_GetsItsList_WithTheHeldKeysTicked()
    {
        var cut = RenderFor("Server contributor", PermissionKeys.ServerView, PermissionKeys.ServerFirewallManage);

        foreach (var area in PermissionCatalog.Areas)
            Assert.NotNull(cut.Find($"#role-permission-keys-{area}"));
        Assert.Equal([PermissionKeys.ServerView, PermissionKeys.ServerFirewallManage], cut.Instance.SelectedIn("server"));
        Assert.Empty(cut.Instance.SelectedIn("pipeline"));
    }

    [Fact]
    public void ABuiltInRole_MarksItsDefaults_AndTheContainerEnvironmentFirewallAndUpgradeAreNotContributorDefaults()
    {
        var cut = RenderFor(RoleNames.Contributor);
        var server = cut.Instance.OptionsOf("server").ToDictionary(option => option.Value, option => option.Text);
        var suffix = server[PermissionKeys.ServerServicesControl][server[PermissionKeys.ServerServicesControl].LastIndexOf('(')..];

        Assert.EndsWith(suffix, server[PermissionKeys.ServerMailboxRead], StringComparison.Ordinal);
        Assert.DoesNotContain(suffix, server[PermissionKeys.ServerDockerContainerEnvView], StringComparison.Ordinal);
        Assert.DoesNotContain(suffix, server[PermissionKeys.ServerManage], StringComparison.Ordinal);
        Assert.DoesNotContain(suffix, server[PermissionKeys.ServerFirewallManage], StringComparison.Ordinal);
        Assert.DoesNotContain(suffix, server[PermissionKeys.ServerSystemUpgrade], StringComparison.Ordinal);
    }

    [Fact]
    public void TheSiteAdmin_HoldsEverything_AndCannotBeEdited()
    {
        var cut = RenderFor(RoleNames.Admin);

        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.Areas.Sum(area => cut.Instance.SelectedIn(area).Count));
        // The only difference with an editable role is the Save button.
        var editable = RenderFor("Server contributor");
        Assert.Equal(editable.FindAll("button").Count - 1, cut.FindAll("button").Count);
    }

    [Fact]
    public async Task Saving_SendsTheTickedKeysInCatalogueOrder()
    {
        _handler.SetResponse(HttpMethod.Put, "api/roles/7/permission-keys", HttpStatusCode.NoContent);
        var saved = false;
        var cut = Render<RolePermissionKeysEditor>(p => p
            .Add(x => x.RoleId, 7)
            .Add(x => x.RoleName, "Server contributor")
            .Add(x => x.Keys, [PermissionKeys.ServerView])
            .Add(x => x.Saved, () => saved = true));

        await cut.InvokeAsync(() => cut.Instance.OnAreaChanged("server",
            [PermissionKeys.ServerFirewallManage, PermissionKeys.ServerView]));
        cut.Render();
        // The Save button closes the editor, after every area list.
        var save = cut.FindAll("button").Last();
        Assert.False(save.HasAttribute("disabled"));
        await cut.InvokeAsync(() => save.Click());

        cut.WaitForState(() => saved);
        var body = _handler.RequestDetails.Single(request => request.Method == "PUT").Body;
        var sent = JsonSerializer.Deserialize<SetRolePermissionKeysRequest>(body!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal([PermissionKeys.ServerView, PermissionKeys.ServerFirewallManage], sent!.Keys);
    }
}
