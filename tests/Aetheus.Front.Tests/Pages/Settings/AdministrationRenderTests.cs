// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Settings;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Settings;

public class AdministrationRenderTests : BunitContext
{
    public AdministrationRenderTests() => BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);

    [Fact]
    public void Renders_AdminUser_ShowsAdminPage()
    {
        var cut = Render<Administration>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        // The admin landing page renders its navigation tiles linking to the admin sub-pages.
        Assert.Contains("href=\"/admin/users\"", cut.Markup);
        Assert.Contains("href=\"/admin/roles\"", cut.Markup);
        Assert.Contains("href=\"/admin/settings\"", cut.Markup);
    }

    [Fact]
    public void OnInitialized_NonAdmin_RedirectsToHome()
    {
        // Register services without admin flag
        using var ctx = new BunitContext();
        BunitTestHelper.RegisterServices(ctx, authenticated: true, isAdmin: false);

        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        ctx.Render<Administration>();

        // The non-admin guard redirects to the home route on init.
        Assert.Contains(nav.History, h => h.Uri == "/");
    }
}
