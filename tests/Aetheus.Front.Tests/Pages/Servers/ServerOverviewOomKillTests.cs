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

    private static AngleSharp.Dom.IElement OomLine(IRenderedComponent<ServerOverviewSection> cut) =>
        cut.FindAll("small").Single(element => element.TextContent.StartsWith("Out-of-memory kills", StringComparison.Ordinal));

    [Fact]
    public void NoKill_IsShownMuted()
    {
        var line = OomLine(RenderSection(0));

        Assert.Equal("Out-of-memory kills since boot: 0", line.TextContent.Trim());
        Assert.Contains("omni-text--muted", line.ClassList);
    }

    [Fact]
    public void KillsSinceBoot_AreShownInDanger()
    {
        var line = OomLine(RenderSection(3));

        Assert.Equal("Out-of-memory kills since boot: 3", line.TextContent.Trim());
        Assert.Contains("omni-text--danger", line.ClassList);
    }

    [Fact]
    public void UnmeasuredOnLinux_ReadsUnknown_NotZero()
    {
        Assert.Equal("Out-of-memory kills: unknown", OomLine(RenderSection(null)).TextContent.Trim());
    }

    [Fact]
    public void Windows_ReadsNotApplicable()
    {
        Assert.Equal("Out-of-memory kills: not applicable",
            OomLine(RenderSection(null, "Microsoft Windows 11 Pro")).TextContent.Trim());
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
