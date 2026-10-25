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

    private static AngleSharp.Dom.IElement InodeLine(IRenderedComponent<ServerOverviewSection> cut) =>
        cut.FindAll("small").Single(element => element.TextContent.StartsWith("Number of files", StringComparison.Ordinal));

    [Fact]
    public void MeasuredInodes_AreShownUnderTheDisk()
    {
        var line = InodeLine(RenderSection(42.5));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, "Number of files: {0}% used", 42.5.ToString("0.#", CultureInfo.CurrentCulture)),
            line.TextContent.Trim());
        Assert.Contains("omni-text--muted", line.ClassList);
    }

    [Fact]
    public void UnmeasuredInodes_ReadAsUnknown_NotZero()
    {
        Assert.Equal("Number of files: unknown", InodeLine(RenderSection(null)).TextContent.Trim());
    }

    [Fact]
    public void NearlyFullInodeTable_IsShownInDanger()
    {
        Assert.Contains("omni-text--danger", InodeLine(RenderSection(97)).ClassList);
    }

    [Fact]
    public void TheDiskTile_ShowsTheFreeSpaceAndAHelpMarkInPlainWords()
    {
        var cut = RenderSection(42.5);

        Assert.Contains("60 Go free of 100 Go", cut.Markup);
        var help = cut.Find("[data-testid='file-count-help']");
        Assert.Contains("full of small files even when space is free", help.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.DoesNotContain("inode", cut.Markup, StringComparison.OrdinalIgnoreCase);
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
