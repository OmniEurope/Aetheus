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
/// PLAN-010 lot 1, R-47: the disk tile shows the fullest file table as a number of files (never the word inode), or says it is unknown. The texts are
/// read from the real English resources so the test sees what an operator reads.
/// </summary>
public sealed class ServerOverviewInodeUsageTests : BunitContext
{
    public ServerOverviewInodeUsageTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<IStringLocalizer<AppStrings>>(new EnglishResourceLocalizer());
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
    }

    private IRenderedComponent<ServerOverviewSection> RenderSection(double? inodeUsedPercent) =>
        Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, new ServerDetailDto
            {
                Id = 1,
                Name = "vps",
                Hostname = "vps.local",
                Type = ServerType.Normal,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                DiskUsedGb = 40,
                DiskTotalGb = 100,
                InodeUsedPercent = inodeUsedPercent
            })
            .Add(x => x.MetricsReceived, true));

    [Theory]
    [InlineData(42.5)]
    [InlineData(97d)]
    [InlineData(null)]
    public void R_10_TheDiskTile_HasNoFileCountLineAndNoHelpMark(double? inodeUsedPercent)
    {
        var cut = RenderSection(inodeUsedPercent);

        Assert.DoesNotContain("Number of files", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=file-count-help]"));
    }

    [Fact]
    public void R_10_TheDiskTile_WritesTheSizesAsWholeNumbers()
    {
        var cut = Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, new ServerDetailDto
            {
                Id = 1,
                Name = "vps",
                Hostname = "vps.local",
                Type = ServerType.Normal,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                DiskUsedGb = 230.6,
                DiskTotalGb = 385.7
            })
            .Add(x => x.MetricsReceived, true));

        Assert.Contains("155 Go free of 386 Go", cut.Markup);
        Assert.DoesNotContain("155,1", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("155.1", cut.Markup, StringComparison.Ordinal);
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
