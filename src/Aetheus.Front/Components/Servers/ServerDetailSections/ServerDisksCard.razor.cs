// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// The filesystems of a server, one block each: free space over total, a usage bar and the number of
/// files. Before the agent has reported its filesystems one by one, the sum the server stores stands in
/// as a single disk, so the section is never empty while a size is known.
/// </summary>
public partial class ServerDisksCard
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;

    /// <summary>One filesystem as the card draws it.</summary>
    internal sealed record DiskRow(
        string Name,
        string FreeOfTotal,
        double UsedGb,
        double TotalGb,
        string UsedText,
        string BarClass,
        string FileCountText,
        OmniTextTone FileCountTone);

    private IReadOnlyList<DiskRow> Rows => BuildRows(Server, L);

    internal static IReadOnlyList<DiskRow> BuildRows(ServerDetailDto server, IStringLocalizer<AppStrings> l)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(l);
        if (server.Disks.Count > 0) return [.. server.Disks.Select(disk => BuildRow(disk.MountPoint, disk, l))];
        if (server.DiskTotalGb <= 0) return [];
        var total = new DiskInfoDto
        {
            UsedGb = server.DiskUsedGb,
            TotalGb = server.DiskTotalGb,
            InodesTotal = null
        };
        return [BuildRow(l["Disk"], total, l, server.InodeUsedPercent)];
    }

    private static DiskRow BuildRow(string name, DiskInfoDto disk, IStringLocalizer<AppStrings> l, double? inodePercent = null)
    {
        var percent = MetricThresholdHelper.Percent(disk.UsedGb, disk.TotalGb);
        var files = FileCount(disk, l, inodePercent);
        return new DiskRow(
            name,
            FreeOfTotal(disk.UsedGb, disk.TotalGb, l),
            disk.UsedGb,
            disk.TotalGb,
            l["DiskUsedPercent", percent.ToString("0.#", CultureInfo.CurrentCulture)],
            MetricThresholdHelper.BarClass(percent),
            files.Text,
            files.Percent is { } p && MetricThresholdHelper.BarClass(p) == "metric-bar-danger"
                ? OmniTextTone.Danger
                : OmniTextTone.Muted);
    }

    /// <summary>"412 Go libres sur 796 Go": what is left first, then what the disk holds.</summary>
    internal static string FreeOfTotal(double usedGb, double totalGb, IStringLocalizer<AppStrings> l) =>
        l["DiskFreeOfTotal", DisplayFormatting.SizeFromGo(Math.Max(0d, totalGb - usedGb)), DisplayFormatting.SizeFromGo(totalGb)];

    // The count of files of one filesystem, "12 345 sur 6 553 600 (3,3 % utilisés)", or unknown (Windows,
    // an older agent, a filesystem without a fixed table). A summed disk only knows the fullest share.
    private static (string Text, double? Percent) FileCount(DiskInfoDto disk, IStringLocalizer<AppStrings> l, double? knownPercent)
    {
        if (disk is { InodesTotal: > 0 and var total, InodesUsed: >= 0 and var used })
        {
            var percent = Math.Round(Math.Min(used, total) * 100d / total, 1);
            return (
                l["FileCountLine",
                    used.ToString("N0", CultureInfo.CurrentCulture),
                    total.ToString("N0", CultureInfo.CurrentCulture),
                    percent.ToString("0.#", CultureInfo.CurrentCulture)],
                percent);
        }
        return knownPercent is { } known
            ? (l["InodesUsedPercent", known.ToString("0.#", CultureInfo.CurrentCulture)], known)
            : (l["InodesUnknown"], null);
    }
}
