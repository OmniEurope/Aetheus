// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerPatchSectionTests : BunitContext
{
    private static ServerSecurityUpdatesDto Known(int total, int security, bool patchCap) => new()
    {
        Known = true,
        PackageManagerPresent = true,
        PatchManagementAvailable = patchCap,
        PendingTotal = total,
        PendingSecurity = security,
        CheckedAt = DateTime.Now,
        Updates =
        [
            new PendingUpdateDto { Package = "openssl", CurrentVersion = "3.0.2", CandidateVersion = "3.0.3", IsSecurity = true },
            new PendingUpdateDto { Package = "vim", CurrentVersion = "8.2", CandidateVersion = "8.3", IsSecurity = false }
        ]
    };

    private IRenderedComponent<ServerPatchSection> RenderWith(
        ServerSecurityUpdatesDto dto, bool? patchCap, ServerDetailDto? server = null, bool isAdmin = false)
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: isAdmin);
        handler.SetJsonResponse(HttpMethod.Get, "security-updates", dto);
        return Render<ServerPatchSection>(p => p
            .Add(c => c.ServerId, 1)
            .Add(c => c.Server, server)
            .Add(c => c.PatchManagementAvailable, patchCap));
    }

    private static ServerDetailDto LinuxServer(string os = "Debian GNU/Linux 12") => new()
    {
        Id = 1,
        Name = "srv-1",
        OsDescription = os,
        AgentCapabilities = [AgentCapabilities.SelfUpdate, AgentCapabilities.EnableCapabilitiesOnUpdate]
    };

    private static ServerSecurityUpdatesDto WithPackages(params string[] packages) => new()
    {
        Known = true,
        PackageManagerPresent = true,
        PendingTotal = packages.Length,
        CheckedAt = DateTime.Now,
        Updates = [.. packages.Select(package => new PendingUpdateDto { Package = package, CurrentVersion = "1", CandidateVersion = "2" })]
    };

    // R-43: the hosting page re-renders on every heartbeat and every click; the section used to read the
    // list again each time and swap the whole page for the loader. It reads again for another server only.
    [Fact]
    public void R43_AParentRerender_DoesNotReloadThePage()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(HttpMethod.Get, "security-updates", Known(2, 1, patchCap: true));
        var cut = Render<ServerPatchSection>(p => p.Add(c => c.ServerId, 1).Add(c => c.PatchManagementAvailable, true));
        var reads = handler.RequestsSnapshot().Count(r => r.Url.Contains("security-updates", StringComparison.Ordinal));

        cut.Render(p => p.Add(c => c.ServerId, 1).Add(c => c.PatchManagementAvailable, false));

        Assert.Equal(1, reads);
        Assert.Equal(reads, handler.RequestsSnapshot().Count(r => r.Url.Contains("security-updates", StringComparison.Ordinal)));
        Assert.Contains("openssl", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R43_AnotherServer_ReadsTheListAgain()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(HttpMethod.Get, "security-updates", Known(2, 1, patchCap: true));
        var cut = Render<ServerPatchSection>(p => p.Add(c => c.ServerId, 1));

        cut.Render(p => p.Add(c => c.ServerId, 2));

        Assert.Equal(2, handler.RequestsSnapshot().Count(r => r.Url.Contains("security-updates", StringComparison.Ordinal)));
    }

    // R-45: no lone shield icon above the badges (the one of the enable-component button stays).
    [Fact]
    public void R45_R29_TheHeaderHoldsNoLoneIcon_OnlyTheActions()
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        handler.SetJsonResponse(HttpMethod.Get, "security-updates", Known(2, 1, patchCap: true));
        var cut = this.RenderWithPageHeader<ServerPatchSection>(p => p
            .Add(c => c.ServerId, 1)
            .Add(c => c.Server, LinuxServer())
            .Add(c => c.PatchManagementAvailable, true));

        Assert.Empty(cut.FindAll(".omni-stack > .omni-icon"));
        // Recette R-29: Apply and its menu sit in the page header, nothing else.
        var header = cut.Find(".test-outlet");
        Assert.Contains("PatchApply", header.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(header.Children, child => child.ClassList.Contains("omni-icon"));
    }

    // R1-5: the hint no longer asks for a command on the host; a button opens the component dialog.
    [Fact]
    public void R1_5_WithoutPatchCapability_OffersAButtonThatOpensTheComponentDialog()
    {
        // R-109: the entry sits in the menu of the page header, so the section is rendered next to the header outlet.
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        handler.SetJsonResponse(HttpMethod.Get, "security-updates", Known(2, 1, patchCap: false));
        var cut = this.RenderWithPageHeader<ServerPatchSection>(p => p
            .Add(c => c.ServerId, 1)
            .Add(c => c.Server, LinuxServer())
            .Add(c => c.PatchManagementAvailable, false));
        var dialogs = Services.GetRequiredService<OmniDialogService>();
        string? openedTitle = null;
        dialogs.OnOpen += (title, _, _, _) => openedTitle = title;

        var button = cut.FindMenuItem("PatchEnableComponent")!;
        Assert.NotNull(button);
        Assert.Contains("patch-enable-component", button.ClassName, StringComparison.Ordinal);
        button.Click();

        Assert.Equal("EnableAgentCapabilities", openedTitle);
    }

    [Fact]
    public void R1_5_TheHintTextCarriesNoInstallerCommand_InEitherLanguage()
    {
        var resources = new System.Resources.ResourceManager("Aetheus.Front.Resources.AppStrings", typeof(Aetheus.Front.Resources.AppStrings).Assembly);
        foreach (var culture in new[] { System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CultureInfo.GetCultureInfo("fr-FR") })
        {
            var hint = resources.GetString("PatchApplyDisabledHint", culture);
            Assert.False(string.IsNullOrWhiteSpace(hint));
            Assert.DoesNotContain("--enable", hint, StringComparison.Ordinal);
            Assert.DoesNotContain("installer", hint, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("installeur", hint, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void R1_5_TheEnableButton_IsAbsent_ForANonAdministrator()
    {
        var cut = RenderWith(Known(2, 1, patchCap: false), patchCap: false, LinuxServer(), isAdmin: false);

        Assert.Contains("PatchApplyDisabledHint", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".patch-enable-component"));
    }

    [Fact]
    public void R1_5_TheEnableButton_IsAbsent_OnAWindowsServer()
    {
        var cut = RenderWith(Known(2, 1, patchCap: false), patchCap: false, LinuxServer("Microsoft Windows Server 2022"), isAdmin: true);

        Assert.Empty(cut.FindAll(".patch-enable-component"));
    }

    [Fact]
    public void R1_5_TheEnableButton_IsAbsent_WhenTheCapabilityIsGranted()
    {
        var cut = RenderWith(Known(2, 1, patchCap: true), patchCap: true, LinuxServer(), isAdmin: true);

        Assert.Empty(cut.FindAll(".patch-enable-component"));
    }

    [Theory]
    [InlineData("docker-ce")]
    [InlineData("containerd.io")]
    public void R1_5_ADockerRestartingPackage_ShowsTheRestartWarning(string package)
    {
        var cut = RenderWith(WithPackages("vim", package), patchCap: true, LinuxServer(), isAdmin: true);

        Assert.Contains("PatchDockerRestartWarning", cut.Find(".patch-docker-restart").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_5_WithoutADockerPackage_NoRestartWarning()
    {
        var cut = RenderWith(WithPackages("vim", "openssl", "docker-ce-cli"), patchCap: true, LinuxServer(), isAdmin: true);

        Assert.Empty(cut.FindAll(".patch-docker-restart"));
    }

    [Fact]
    public void KnownStatus_RendersCountsAndPackages()
    {
        var cut = RenderWith(Known(2, 1, patchCap: true), patchCap: true);

        Assert.Contains("openssl", cut.Markup);
        Assert.Contains("vim", cut.Markup);
        Assert.Contains("PatchSecurityCount", cut.Markup); // security badge label key
        Assert.Contains("PatchPendingCount", cut.Markup);
    }

    [Fact]
    public void WithoutPatchCapability_ShowsApplyDisabledHint()
    {
        var cut = RenderWith(Known(2, 1, patchCap: false), patchCap: false);

        Assert.Contains("PatchApplyDisabledHint", cut.Markup);
    }

    [Fact]
    public void WithPatchCapability_NoDisabledHint()
    {
        var cut = RenderWith(Known(2, 1, patchCap: true), patchCap: true);

        Assert.DoesNotContain("PatchApplyDisabledHint", cut.Markup);
    }

    [Fact]
    public void NotAptServer_ShowsNotApplicable_NotZero()
    {
        var cut = RenderWith(new ServerSecurityUpdatesDto { Known = false, PackageManagerPresent = false }, patchCap: null);

        Assert.Contains("PatchNotApplicableTitle", cut.Markup);
    }

    // R-119: the task an upgrade check queues is followed on the page (and veils the section until it ends).
    [Fact]
    public void R119_AQueuedUpgradeCheck_IsFollowedOnThePage()
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        handler.SetJsonResponse(HttpMethod.Get, "security-updates", Known(2, 1, patchCap: true));
        handler.SetJsonResponse(HttpMethod.Post, "system/upgrade", new ServiceTaskResponse { TaskId = 12 });
        var cut = this.RenderWithPageHeader<ServerPatchSection>(p => p
            .Add(c => c.ServerId, 1)
            .Add(c => c.Server, LinuxServer())
            .Add(c => c.PatchManagementAvailable, true));
        Assert.Empty(cut.FindAll(".server-task-follower"));

        cut.FindMenuItem("PatchCheck")!.Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".server-task-follower")));
        Assert.Contains("/tasks/12", cut.Find(".server-task-follower").InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeNeverSucceeded_ShowsUnknown_NotUpToDate()
    {
        var cut = RenderWith(new ServerSecurityUpdatesDto { Known = false, PackageManagerPresent = true }, patchCap: true);

        Assert.Contains("PatchUnknown", cut.Markup);
    }
}
