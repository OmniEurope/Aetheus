// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

public class ComplexityAnalyzerTests
{
    [Fact]
    public void Analyze_ComputesPerMethodCyclomaticComplexity()
    {
        const string src = """
            namespace X;
            public class C
            {
                public int Simple() => 1;
                public int Branchy(int n)
                {
                    if (n > 0 && n < 10) { }
                    for (var i = 0; i < n; i++) { }
                    switch (n) { case 1: break; case 2: break; default: break; }
                    return n;
                }
            }
            """;

        var report = ComplexityAnalyzer.Analyze([("C.cs", src)]);

        // Simple = 1; Branchy = 1 + if + && + for + case 1 + case 2 = 6 (default label does not count).
        Assert.Equal(2, report.TotalMethods);
        Assert.Equal(6, report.MaxCyclomatic);
        Assert.Equal(3.5, report.AvgCyclomatic, 2);
        Assert.Equal(0, report.HighComplexityMethods);
        Assert.True(report.TotalLinesOfCode > 0);
        Assert.Equal("Branchy", report.Hotspots[0].Member);
        Assert.Equal("C.cs", report.Hotspots[0].Path);
        Assert.Equal(5, report.Hotspots[0].Line);
    }

    [Fact]
    public void Analyze_FlagsMethodsOverThreshold()
    {
        // A method with 12 if-statements → CC 13 (> 10 threshold).
        var ifs = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"if (n == {i}) {{ }}"));
        var src = $$"""
            public class C
            {
                public void Hot(int n)
                {
                    {{ifs}}
                }
            }
            """;

        var report = ComplexityAnalyzer.Analyze([("C.cs", src)]);

        Assert.Equal(1, report.TotalMethods);
        Assert.Equal(13, report.MaxCyclomatic);
        Assert.Equal(1, report.HighComplexityMethods);
        Assert.Equal(13, report.Hotspots[0].Cyclomatic);
    }

    [Fact]
    public void Analyze_NoMethods_ReturnsZeroComplexityButCountsLoc()
    {
        const string src = """
            namespace X;
            public class C
            {
                public int Value;
            }
            """;

        var report = ComplexityAnalyzer.Analyze([("C.cs", src)]);

        Assert.Equal(0, report.TotalMethods);
        Assert.Equal(0, report.MaxCyclomatic);
        Assert.Equal(0, report.AvgCyclomatic);
        Assert.Empty(report.Hotspots);
        Assert.True(report.TotalLinesOfCode > 0);
    }

    [Fact]
    public void Analyze_AttributesLambdaAndLocalFunctionComplexityToTheirOwnCallable()
    {
        const string src = """
            public class C
            {
                public int Outer(int n)
                {
                    int Local(int value) => value > 0 && value < 10 ? value : 0;
                    return new[] { n }.Where(value => value > 0 || value < -10).Sum(Local);
                }
            }
            """;

        var report = ComplexityAnalyzer.Analyze([("C.cs", src)]);

        Assert.Equal(3, report.TotalMethods);
        Assert.Equal(3, report.MaxCyclomatic);
        Assert.Contains(report.Hotspots, item => item.Member == "Local" && item.Cyclomatic == 3);
        Assert.Contains(report.Hotspots, item => item.Member == "<lambda>" && item.Cyclomatic == 2);
        Assert.Contains(report.Hotspots, item => item.Member == "Outer" && item.Cyclomatic == 1);
    }

    /// <summary>
    /// Recette R2-107: the nightly's "Measure complexity" stage runs this analyzer over every C# file of
    /// the checkout, tests and migrations included, and grades its maximum (A at 15 or less). This holds
    /// the same file set to the same threshold, so a method that would cost the grade fails here first.
    /// </summary>
    [Fact]
    public void Repository_RemainsWithinGradeAComplexityThreshold()
    {
        var root = FindRepoRoot();
        var separator = Path.DirectorySeparatorChar;
        var sources = RepositoryScan.Enumerate(root, "*.cs")
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{separator}node_modules{separator}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{separator}.git{separator}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{separator}.vs{separator}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{separator}.codex-runtime{separator}", StringComparison.OrdinalIgnoreCase)
                // Other checkouts nested under .claude/worktrees (other sessions, other branches, their
                // uncommitted work) are not this tree's code: one of them failed this budget on a push.
                // .worktrees holds the manual ones (git-ignored) for the same reason.
                && !path.Contains($"{separator}.claude{separator}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{separator}.worktrees{separator}", StringComparison.OrdinalIgnoreCase))
            .Select(path => (path, File.ReadAllText(path)));

        var report = ComplexityAnalyzer.Analyze(sources);
        var details = string.Join(
            Environment.NewLine,
            report.Hotspots.Select(item =>
                $"CC {item.Cyclomatic}: {Path.GetRelativePath(root, item.Path)}:{item.Line} ({item.Member})"));

        Assert.True(
            report.MaxCyclomatic <= 15,
            $"Repository max cyclomatic complexity is {report.MaxCyclomatic}; grade A of the nightly quality grade (grade.complexity.maximum) requires <= 15.{Environment.NewLine}{details}");
    }

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
