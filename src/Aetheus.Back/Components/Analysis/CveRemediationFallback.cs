// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Inventory;

namespace Aetheus.Back.Components.Analysis;

/// <summary>What one match needs, for the command a person can run themselves.</summary>
/// <param name="SourcePath">The file that names the package, or null when the inventory holds none. An inventory origin (the SBOM) is no file.</param>
/// <param name="Kind">What the inventory item is: a runtime, a base image or a system package is never installed by a package manager command.</param>
public sealed record CveRemediationCommandInput(
    string Ecosystem, string PackageName, string? TargetVersion, string? SourcePath, ProjectInventoryItemKind? Kind = null);

/// <summary>
/// PLAN-004, lot 4, point 13: the command to run when the platform does not do the change itself, with its reason. A pure function.
/// A package name comes from an advisory: a name or a version that is not plainly one gives no command, because a command a person
/// pastes in a terminal is not a place to carry an unchecked text. No command is ever invented for an ecosystem it does not know.
/// </summary>
public static partial class CveRemediationFallback
{
    public static CveRemediationFallbackDto For(CveRemediationCommandInput input, CveRemediationFallbackReason reason, string detail)
    {
        ArgumentNullException.ThrowIfNull(input);
        var file = InventoryOrigins.IsFile(input.SourcePath) ? input.SourcePath : null;
        return new CveRemediationFallbackDto
        {
            Reason = reason,
            Detail = detail,
            Command = CommandOf(input),
            WorkingDirectory = file is null ? null : DirectoryOf(file),
            Files = file is null ? [] : [file]
        };
    }

    private static string? CommandOf(CveRemediationCommandInput input)
    {
        // A runtime, a base image or a system package comes from the SDK or the image, never from a package to add: a runtime pack
        // (Microsoft.NETCore.App.Runtime.linux-x64) is a NuGet name, and `dotnet add package` on it would be wrong.
        if (input.Kind is ProjectInventoryItemKind.Runtime or ProjectInventoryItemKind.BaseImage or ProjectInventoryItemKind.SystemPackage) return null;
        if (input.Ecosystem == "NuGet" && InventoryOrigins.IsRuntimePack(input.PackageName)) return null;
        if (input.TargetVersion is not { } target || !SafeVersion().IsMatch(target) || !SafeName().IsMatch(input.PackageName)) return null;
        var name = input.PackageName;
        return input.Ecosystem switch
        {
            "NuGet" => $"dotnet add package {name} --version {target}",
            "npm" => $"npm install {name}@{target} --ignore-scripts",
            "Packagist" => $"composer require {name}:{target} --no-scripts --no-plugins",
            "PyPI" => $"pip install {name}=={target}",
            "Go" => $"go get {name}@v{target.TrimStart('v')}",
            "crates.io" => $"cargo update -p {name} --precise {target}",
            "RubyGems" => $"bundle update {name} --conservative",
            _ => null
        };
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.Replace('\\', '/').LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    [GeneratedRegex(@"^@?[A-Za-z0-9][A-Za-z0-9._~\-]*(/[A-Za-z0-9][A-Za-z0-9._~\-]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^v?\d+(\.\d+){0,3}([\-+][0-9A-Za-z.\-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVersion();
}
