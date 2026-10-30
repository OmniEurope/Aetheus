// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-12 (2026-10-25): one server name can be served by two files on the same port (production had
/// <c>aetheus-api.sonytumen.com.conf</c> and <c>api.aetheus.sonytumen.com.conf</c>, both on :80). Each
/// site button then reads « :80 », so its title names the file it disables or enables.
/// </summary>
public sealed class ApacheSiteToggleTitleTests : BunitContext
{
    public ApacheSiteToggleTitleTests() => BunitTestHelper.RegisterServices(this);

    private IRenderedComponent<ServerApacheSection> RenderSection(bool enabled) =>
        Render<ServerApacheSection>(parameters => parameters
            .Add(section => section.ServerId, 41)
            .Add(section => section.Server, new ServerDetailDto
            {
                Id = 41,
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
                    VirtualHosts =
                    [
                        new ApacheVirtualHostDto { ServerName = "api.example.test", Port = 80, IsEnabled = enabled, ConfigFile = "/etc/apache2/sites-enabled/old-api.example.test.conf" },
                        new ApacheVirtualHostDto { ServerName = "api.example.test", Port = 80, IsEnabled = enabled, ConfigFile = "/etc/apache2/sites-enabled/api.example.test.conf" }
                    ]
                }
            }));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoFilesOnOnePort_EachButtonNamesItsFile(bool enabled)
    {
        var cut = RenderSection(enabled);

        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll("button[title]")
                .Select(button => button.GetAttribute("title")!)
                .Where(title => title.EndsWith("(80)", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, titles.Count);
            Assert.Single(titles, title => title.Contains("old-api.example.test.conf", StringComparison.Ordinal));
            Assert.Single(titles, title => title.Contains(" api.example.test.conf", StringComparison.Ordinal));
        });
    }
}
