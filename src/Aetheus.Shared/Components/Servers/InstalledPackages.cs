// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Shared.Components.Servers;

/// <summary>
/// Recette R3-59: the output contract of an <see cref="Aetheus.Shared.Components.Tasks.OperationKind.PackageInventory"/>
/// task, written by the agent and read back by the Services page. One <c>PACKAGE name version</c> line per
/// installed package, tab-separated, then one closing <c>TOTAL n</c> line. A list whose closing line is
/// missing, or whose count does not match the lines received, is incomplete and never shown as the full list.
/// </summary>
public static class InstalledPackages
{
    /// <summary>The task name, also how the Services page finds an inventory still running.</summary>
    public const string TaskName = "Installed packages - read-only list";

    public const string PackagePrefix = "PACKAGE";
    public const string TotalPrefix = "TOTAL";

    public static string PackageLine(string name, string version) =>
        $"{PackagePrefix}\t{Clean(name)}\t{Clean(version)}";

    public static string TotalLine(int count) =>
        $"{TotalPrefix}\t{count.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Parses a task output. Lines that are not part of the contract (agent notices) are ignored.</summary>
    public static InstalledPackagesReportDto Parse(string? output)
    {
        var packages = new List<InstalledPackageDto>();
        int? total = null;
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length >= 2 && parts[0] == PackagePrefix && parts[1].Trim().Length > 0)
                packages.Add(new InstalledPackageDto { Name = parts[1].Trim(), Version = parts.Length > 2 ? parts[2].Trim() : string.Empty });
            else if (parts.Length >= 2 && parts[0] == TotalPrefix
                     && int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                total = count;
        }
        return new InstalledPackagesReportDto
        {
            Packages = [.. packages.OrderBy(p => p.Name, StringComparer.Ordinal)],
            ExpectedCount = total,
            IsComplete = total is { } expected && expected == packages.Count
        };
    }

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
}

/// <summary>One installed package: its name and installed version, as the package database reports them.</summary>
public sealed record InstalledPackageDto
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
}

/// <summary>The parsed inventory. <see cref="IsComplete"/> is true only when the closing <c>TOTAL</c> line
/// arrived and matches the package lines received.</summary>
public sealed record InstalledPackagesReportDto
{
    public bool IsComplete { get; init; }
    public int? ExpectedCount { get; init; }
    public List<InstalledPackageDto> Packages { get; init; } = [];
}
