// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Inventory;

/// <summary>
/// The images of the servers of a project as inventory entries. A server lists an image by repository and tag only: what
/// the image holds is not known without its SBOM, so every image is a <c>no-sbom</c> gap and the inventory is never
/// <c>Complete</c>. A server several projects share gives its images to none of them, and says so, rather than guess.
/// </summary>
internal static class ServerInventoryMapper
{
    private const string Untagged = "<none>";
    private const string DockerTechnology = "docker";

    public static (List<InventoryEntry> Entries, List<InventoryGap> Gaps) Map(ProjectServerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entries = new List<InventoryEntry>();
        var gaps = new List<InventoryGap>();
        foreach (var server in snapshot.Servers)
        {
            foreach (var image in server.Images)
            {
                if (IsUntagged(image))
                {
                    gaps.Add(new InventoryGap(InventoryGapCodes.UntaggedImage));
                    continue;
                }

                entries.Add(new InventoryEntry(
                    InventoryEcosystems.Docker, image.Repository, image.Tag, IsDirect: true, ProjectInventoryItemKind.BaseImage, $"{InventoryOrigins.ServerPrefix}{server.ServerName}"));
                gaps.Add(new InventoryGap(InventoryGapCodes.NoSbom, DockerTechnology));
            }
        }

        gaps.AddRange(Enumerable.Repeat(new InventoryGap(InventoryGapCodes.SharedServer), snapshot.SharedServers));
        gaps.AddRange(Enumerable.Repeat(new InventoryGap(InventoryGapCodes.ServerNotRegistered), snapshot.UnregisteredLinks));
        return (entries, gaps);
    }

    private static bool IsUntagged(ServerImage image) =>
        string.IsNullOrWhiteSpace(image.Repository) || string.IsNullOrWhiteSpace(image.Tag)
        || image.Repository == Untagged || image.Tag == Untagged;
}
