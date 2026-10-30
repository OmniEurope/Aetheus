// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;

namespace Aetheus.Front.Tests.Pages.Layout;

/// <summary>R-64: the project sub-menu and the overview's Sections tiles share one catalog and one order.</summary>
public sealed class ProjectNavigationOrderTests
{
    [Fact]
    public void VisibleItems_ListServersBetweenVaultsAndEnvironments()
    {
        var permissions = new PermissionService();
        permissions.SetPermissions([], isAdmin: true);

        var slugs = ProjectNavigationCatalog.VisibleItems(permissions, 1).Select(item => item.Slug).ToList();

        Assert.Equal(
            ["overview", "pipelines", "quality", "releases", "artifacts", "libraries", "vaults", "servers",
             "environments", "monitoring", "tasks", "logs", "git", "edit"],
            slugs);
    }
}
