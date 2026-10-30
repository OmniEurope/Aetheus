// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-119: a disabled module offers Enable; a module Debian's a2dismod refuses to disable (deflate) offers
/// no Disable button and is labelled essential instead.</summary>
public sealed class ApacheModuleActionsTests : BunitContext
{
    public ApacheModuleActionsTests() => BunitTestHelper.RegisterServices(this);

    private IRenderedComponent<ServerApacheSection> RenderModulesTab()
    {
        var cut = Render<ServerApacheSection>(parameters => parameters
            .Add(section => section.ServerId, 42)
            .Add(section => section.Server, new ServerDetailDto
            {
                Id = 42,
                Name = "web-srv",
                Hostname = "10.0.0.5",
                Type = ServerType.Normal,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
                Apache = new ApacheDataDto
                {
                    IsInstalled = true,
                    IsRunning = true,
                    Modules =
                    [
                        new ApacheModuleDto { Name = "headers_module", Type = "shared", IsEnabled = false },
                        new ApacheModuleDto { Name = "deflate_module", Type = "shared", IsEnabled = true },
                        new ApacheModuleDto { Name = "rewrite_module", Type = "shared", IsEnabled = true }
                    ]
                }
            }));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='tab']")));
        cut.FindAll("[role='tab']").Single(tab => tab.TextContent.Contains("Modules", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Contains("headers_module", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static IReadOnlyList<string> ButtonTitles(IRenderedComponent<ServerApacheSection> cut) =>
        cut.FindAll("button[title]").Select(button => button.GetAttribute("title")!).ToList();

    [Fact]
    public void DisabledModule_ShowsAnEnableButton()
    {
        var cut = RenderModulesTab();

        Assert.Contains("Enable", ButtonTitles(cut));
    }

    [Fact]
    public void EssentialModule_DeflateShowsNoDisableButton_ButANonEssentialOneDoes()
    {
        var cut = RenderModulesTab();

        Assert.Single(ButtonTitles(cut), title => title == "Disable");
        Assert.Contains("ApacheModuleEssential", cut.Markup, StringComparison.Ordinal);
    }
}
