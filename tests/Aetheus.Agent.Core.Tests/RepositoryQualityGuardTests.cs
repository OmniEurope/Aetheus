// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

public sealed class RepositoryQualityGuardTests
{
    [Fact]
    public void ProductComplexity_RemainsWithinGradeAThreshold()
    {
        var root = FindRepoRoot();
        var sources = RepositoryScan.Enumerate(root, "*.cs")
            .Where(path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !RepositoryPathFilter.IsNestedCheckout(root, path))
            .Select(path => (path, File.ReadAllText(path)))
            .ToList();
        var productSources = ArchitectureAnalysisPublisher.SelectProductSources(root, sources);

        var report = ComplexityAnalyzer.Analyze(productSources);
        var details = string.Join(
            Environment.NewLine,
            report.Hotspots
                .Where(item => item.Cyclomatic > 15)
                .Select(item => $"{item.Cyclomatic}: {Path.GetRelativePath(root, item.Path)}:{item.Line} {item.Member}"));

        Assert.True(report.MaxCyclomatic <= 15,
            $"Maximum cyclomatic complexity is {report.MaxCyclomatic}; grade A requires <= 15.{Environment.NewLine}{details}");
    }

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
