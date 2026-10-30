// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Resources;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Resources;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// PLAN-010 lot 1: the memory tile shows the OOM kills since boot, unknown when the agent does not
/// measure them, and not applicable on Windows. Texts come from the real English resources.
/// </summary>
public sealed class ServerOverviewOomKillTests : BunitContext
{
    public ServerOverviewOomKillTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<IStringLocalizer<AppStrings>>(new EnglishResourceLocalizer());
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
    }

    private IRenderedComponent<ServerOverviewSection> RenderSection(long? oomKills, string os = "Ubuntu 24.04 LTS") =>
        Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, new ServerDetailDto
            {
                Id = 1,
                Name = "vps",
                Hostname = "vps.local",
                OsDescription = os,
                Type = ServerType.Normal,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                MemoryUsedMb = 1024,
                MemoryTotalMb = 4096,
                OomKillsSinceBoot = oomKills
            })
            .Add(x => x.MetricsReceived, true));

    [Theory]
    [InlineData(0L, "Ubuntu 24.04 LTS")]
    [InlineData(null, "Ubuntu 24.04 LTS")]
    [InlineData(null, "Microsoft Windows 11 Pro")]
    public void R_10_NoKillLine_WhenThereIsNoKill(long? oomKills, string os)
    {
        var cut = RenderSection(oomKills, os);

        Assert.DoesNotContain("Out-of-memory", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void KillsSinceBoot_AreShownInDanger()
    {
        var line = RenderSection(3).FindAll("small").Single(element => element.TextContent.StartsWith("Out-of-memory kills", StringComparison.Ordinal));

        Assert.Equal("Out-of-memory kills since boot: 3", line.TextContent.Trim());
        Assert.Contains("omni-text--danger", line.ClassList);
    }

    private sealed class EnglishResourceLocalizer : IStringLocalizer<AppStrings>
    {
        private static readonly ResourceManager Resources =
            new("Aetheus.Front.Resources.AppStrings", typeof(AppStrings).Assembly);

        public LocalizedString this[string name] =>
            Resources.GetString(name, CultureInfo.InvariantCulture) is { } value
                ? new LocalizedString(name, value)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.CurrentCulture, this[name].Value, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
