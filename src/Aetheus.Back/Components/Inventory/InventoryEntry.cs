// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Inventory;

/// <summary>A package as a manifest states it, before normalization.</summary>
internal sealed record InventoryEntry(
    string Ecosystem,
    string Name,
    string Version,
    bool IsDirect,
    ProjectInventoryItemKind Kind = ProjectInventoryItemKind.Dependency,
    string? SourcePath = null);

/// <summary>Why an inventory is not complete. Stored as <c>code:technology=count</c>, for the page to translate.</summary>
internal sealed record InventoryGap(string Code, string? Technology = null);

/// <summary>The ecosystem names of OSV, plus the two that have no advisory feed of their own yet.</summary>
internal static class InventoryEcosystems
{
    public const string NuGet = "NuGet";
    public const string Npm = "npm";
    public const string Packagist = "Packagist";
    public const string PyPI = "PyPI";
    public const string Maven = "Maven";
    public const string Go = "Go";
    public const string Crates = "crates.io";
    public const string RubyGems = "RubyGems";
    public const string Debian = "Debian";
    public const string Alpine = "Alpine";
    public const string Ubuntu = "Ubuntu";
    public const string Docker = "docker";

    /// <summary>A runtime named as in the endoflife.date products: dotnet, nodejs, php, go.</summary>
    public const string Runtime = "runtime";
}

/// <summary>The stable reason codes of <see cref="InventoryGap"/>.</summary>
internal static class InventoryGapCodes
{
    public const string NoRepository = "no-repository";
    public const string AddressOnly = "repository-address-only";
    public const string RepositoryEmpty = "repository-empty";
    public const string RepositoryUnreadable = "repository-unreadable";
    public const string ReleaseCommitNotFound = "release-commit-not-found";
    public const string NoManifest = "no-recognized-manifest";
    public const string NoLockFile = "no-lock-file";
    public const string NotRead = "technology-not-read";
    public const string Unreadable = "file-unreadable";
    public const string FileTooLarge = "file-too-large";
    public const string FileLimit = "file-limit";
    public const string ItemLimit = "item-limit";
    public const string Unpinned = "unpinned-version";
    public const string UnresolvedImage = "unresolved-image";
    public const string Oversized = "oversized-entry";
    public const string NoSbom = "no-sbom";
    public const string SbomTruncated = "sbom-truncated";
    public const string UnmappedComponent = "unmapped-component";

    /// <summary>A server linked to several projects: nothing says which of them runs its images, so none gets them.</summary>
    public const string SharedServer = "shared-server";

    /// <summary>A project server that names no registered server (an external host): no agent reports its images.</summary>
    public const string ServerNotRegistered = "server-not-registered";

    /// <summary>An image with no repository or no tag (<c>&lt;none&gt;</c>): it names nothing to look an advisory up by.</summary>
    public const string UntaggedImage = "untagged-image";
}
