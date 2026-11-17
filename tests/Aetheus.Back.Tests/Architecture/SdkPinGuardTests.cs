// kit-model SdkPinGuardTests 1
// SPDX-License-Identifier: EUPL-1.2
//
// Guard test for rule STD-SDKPIN (docs/code-rules.md): the three Microsoft.CodeAnalysis.* packages
// must stay on the Roslyn version shipped by the compiler of the global.json SDK floor. An analyzer
// compiled against a newer Roslyn than the host compiler is rejected (CS9057) on every machine still
// at the floor.
//
// Copy as is into a test project (NUnit) and adjust only the namespace. It walks up from the test
// binary to the repository root (the folder holding global.json), so no path constant is needed.
// The guard checks the PIN, never the floor itself: bumping global.json is a deliberate commit that
// moves both files together.
// Keep the first line of this file in the copy: it tells the kit's verify-rules.ps1 which version of
// the model the copy implements (STD-KITCOPY).
//
// Aetheus copy: the NUnit assertions are written in xUnit, the test framework of this project.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Aetheus.Back.Tests.Architecture;

public sealed class SdkPinGuardTests
{
    /// <summary>SDK feature band of the floor -> Roslyn version its compiler ships.</summary>
    private static readonly (string Band, string Roslyn)[] RoslynByBand =
    [
        ("9.0.1", "4.12.0"),
        ("10.0.1", "5.0.0"),
        ("10.0.2", "5.3.0"),
        ("10.0.3", "5.6.0"),
        ("10.0.4", "5.9.0"),
        // SDK 11.0.1xx ships Roslyn 5.12.0, which nuget.org does not publish as a stable package (latest: 5.9.0,
        // 2026-11-11). An analyzer built on 5.9.0 loads under a 5.12 host compiler (only a newer one is refused,
        // CS9057), so the pin stays on the newest stable. Move it to 5.12.x when nuget.org carries it (ADR-048).
        ("11.0.1", "5.9.0"),
    ];

    private static readonly string[] PinnedPackages =
    [
        "Microsoft.CodeAnalysis.CSharp",
        "Microsoft.CodeAnalysis.CSharp.Workspaces",
        "Microsoft.CodeAnalysis.Workspaces.Common",
    ];

    [Fact]
    public void CodeAnalysisPackagesMatchTheSdkFloorCompiler()
    {
        var root = FindRepositoryRoot();
        var floor = ReadSdkFloor(Path.Combine(root, "global.json"));
        var expected = ExpectedRoslyn(floor);

        var packagesProps = Path.Combine(root, "Directory.Packages.props");
        Assert.True(File.Exists(packagesProps),
            $"Directory.Packages.props introuvable a la racine du depot ({root}).");

        var content = File.ReadAllText(packagesProps);
        var mismatches = PinnedPackages
            .Select(package => (Package: package, Version: ReadPackageVersion(content, package)))
            .Where(entry => entry.Version is not null && entry.Version != expected)
            .Select(entry => $"{entry.Package} = {entry.Version} (attendu {expected})")
            .ToArray();

        Assert.True(mismatches.Length == 0,
            $"Plancher SDK {floor} : Roslyn attendu {expected}. Desalignement -> "
            + string.Join(" ; ", mismatches)
            + ". Voir STD-SDKPIN dans docs/code-rules.md.");
    }

    private static string ExpectedRoslyn(string floor)
    {
        var match = RoslynByBand.FirstOrDefault(entry => floor.StartsWith(entry.Band, StringComparison.Ordinal));
        Assert.True(match.Roslyn is not null,
            $"Plancher SDK {floor} absent de la table STD-SDKPIN : completer RoslynByBand et la table de docs/code-rules.md.");
        return match.Roslyn;
    }

    private static string ReadSdkFloor(string globalJsonPath)
    {
        Assert.True(File.Exists(globalJsonPath), $"global.json introuvable : {globalJsonPath}");
        using var document = JsonDocument.Parse(File.ReadAllText(globalJsonPath));
        var version = document.RootElement.GetProperty("sdk").GetProperty("version").GetString();
        Assert.False(string.IsNullOrEmpty(version), "global.json : sdk.version vide.");
        return version!;
    }

    /// <summary>Version of a PackageVersion/PackageReference entry, or null when the package is absent.</summary>
    private static string? ReadPackageVersion(string content, string package)
    {
        var pattern = @"<Package(?:Version|Reference)\s+Include=""" + Regex.Escape(package)
            + @"""\s+Version=""([^""]+)""";
        var match = Regex.Match(content, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Aucun global.json trouve en remontant depuis le binaire de test.");
        return directory!.FullName;
    }

    // Aetheus extension beyond the kit model (candidate for the model): the model skips a package that
    // is not pinned, this copy also requires the three packages to be pinned, as its earlier version did.

    [Fact]
    public void CodeAnalysisPackagesArePinned()
    {
        var content = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Directory.Packages.props"));
        var unpinned = PinnedPackages.Where(package => ReadPackageVersion(content, package) is null).ToArray();
        Assert.True(unpinned.Length == 0,
            $"Directory.Packages.props n'epingle pas : {string.Join(", ", unpinned)}. Voir STD-SDKPIN dans docs/code-rules.md.");
    }
}
