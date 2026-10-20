// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.Architecture;

public class AnalyzerDistributionAuditTests
{
    [Fact]
    public void BlockingRulesUseSdkRazorInputsWithoutDuplicates()
    {
        var root = FindRepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var targets = File.ReadAllText(Path.Combine(root, "Directory.Build.targets"));
        var frontProject = File.ReadAllText(Path.Combine(
            root, "src", "Aetheus.Front", "Aetheus.Front.csproj"));

        Assert.Contains("PRM001;PRM002;PRM003", props, StringComparison.Ordinal);
        Assert.DoesNotContain("<AdditionalFiles Include=", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("<AdditionalFiles Include=", frontProject, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProductProjectReferencesTheAnalyzer()
    {
        var root = FindRepoRoot();
        var sourceDirectory = Path.Combine(root, "src");
        var projects = RepositoryScan.Enumerate(sourceDirectory, "*.csproj")
            .Where(path => !path.EndsWith(
                Path.Combine("Aetheus.Analyzers", "Aetheus.Analyzers.csproj"),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(projects);
        var missing = projects
            .Where(path => !File.ReadAllText(path).Contains(
                "Aetheus.Analyzers\\Aetheus.Analyzers.csproj",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void AnalyzerPackageUsesCentralVersionAndReleaseTracking()
    {
        var root = FindRepoRoot();
        var project = File.ReadAllText(Path.Combine(
            root, "src", "Aetheus.Analyzers", "Aetheus.Analyzers.csproj"));

        Assert.DoesNotContain("<Version>1.0.0</Version>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("RS2008", project, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            root, "src", "Aetheus.Analyzers", "AnalyzerReleases.Shipped.md")));
        Assert.True(File.Exists(Path.Combine(
            root, "src", "Aetheus.Analyzers", "AnalyzerReleases.Unshipped.md")));
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
