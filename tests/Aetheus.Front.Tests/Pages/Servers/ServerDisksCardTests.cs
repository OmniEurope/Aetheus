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
/// R-47: the storage section says how much is FREE ("x Go free of y Go"), draws one usage bar per
/// filesystem with an OE progress bar, and shows the number of files with a help mark, never the word
/// inode. The texts are read from the real English resources.
/// </summary>
public sealed class ServerDisksCardTests : BunitContext
{
    public ServerDisksCardTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<IStringLocalizer<AppStrings>>(new EnglishResourceLocalizer());
    }

    private static ServerDetailDto Server(params DiskInfoDto[] disks) => new()
    {
        Id = 1,
        Name = "vps",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
        Disks = [.. disks],
        DiskUsedGb = disks.Sum(disk => disk.UsedGb),
        DiskTotalGb = disks.Sum(disk => disk.TotalGb)
    };

    private IRenderedComponent<ServerDisksCard> RenderCard(ServerDetailDto server) =>
        Render<ServerDisksCard>(p => p.Add(x => x.Server, server));

    [Fact]
    public void EachFilesystem_ShowsItsFreeSpaceOverItsTotal_AndAUsageBar()
    {
        var cut = RenderCard(Server(
            new DiskInfoDto { MountPoint = "/", UsedGb = 100, TotalGb = 400, InodesTotal = 26_214_400, InodesUsed = 860_000 },
            new DiskInfoDto { MountPoint = "/mnt/volume", UsedGb = 10, TotalGb = 100 }));

        var items = cut.FindAll("[data-testid='server-disk-item']");
        Assert.Equal(2, items.Count);
        Assert.Contains("/", items[0].TextContent);
        Assert.Contains("300 Go free of 400 Go", items[0].TextContent);
        Assert.Contains("90 Go free of 100 Go", items[1].TextContent);
        var bar = items[0].QuerySelector("[role='progressbar']");
        Assert.NotNull(bar);
        Assert.Equal("25% used", bar!.GetAttribute("aria-valuetext"));
    }

    [Fact]
    public void TheNumberOfFiles_IsWrittenInPlainWords_WithItsHelpMark_AndNeverSaysInode()
    {
        var cut = RenderCard(Server(
            new DiskInfoDto { MountPoint = "/", UsedGb = 10, TotalGb = 100, InodesTotal = 1000, InodesUsed = 250 },
            new DiskInfoDto { MountPoint = "/other", UsedGb = 10, TotalGb = 100 }));

        var items = cut.FindAll("[data-testid='server-disk-item']");
        Assert.Contains("Number of files: ", items[0].TextContent);
        Assert.Contains("(25% used)", items[0].TextContent);
        Assert.Contains("Number of files: unknown", items[1].TextContent);
        Assert.All(items, item => Assert.NotNull(item.QuerySelector("[data-testid='file-count-help']")));
        Assert.DoesNotContain("inode", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GB", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("GiB", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void BeforeTheDisksAreReported_TheStoredSumStandsInAsOneDisk()
    {
        var server = new ServerDetailDto
        {
            Id = 1,
            Name = "vps",
            Type = ServerType.Normal,
            Status = ServerStatus.Online,
            Tags = [],
            Services = [],
            DiskUsedGb = 40,
            DiskTotalGb = 100,
            InodeUsedPercent = 12
        };

        var item = Assert.Single(RenderCard(server).FindAll("[data-testid='server-disk-item']"));

        Assert.Contains("60 Go free of 100 Go", item.TextContent);
        Assert.Contains("Number of files: 12% used", item.TextContent);
    }

    [Fact]
    public void WithoutAnySize_TheCardSaysUnavailable()
    {
        var server = new ServerDetailDto
        {
            Id = 1,
            Name = "vps",
            Type = ServerType.Normal,
            Status = ServerStatus.Online,
            Tags = [],
            Services = []
        };

        var cut = RenderCard(server);

        Assert.Empty(cut.FindAll("[data-testid='server-disk-item']"));
        Assert.Contains("Unavailable", cut.Markup);
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
