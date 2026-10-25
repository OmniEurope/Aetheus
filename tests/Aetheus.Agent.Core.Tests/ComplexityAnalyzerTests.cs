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
        // A method with 16 if-statements → CC 17 (> 15 threshold).
        var ifs = string.Join("\n", Enumerable.Range(0, 16).Select(i => $"if (n == {i}) {{ }}"));
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
        Assert.Equal(17, report.MaxCyclomatic);
        Assert.Equal(1, report.HighComplexityMethods);
        Assert.Equal(17, report.Hotspots[0].Cyclomatic);
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

    /// <summary> The nightly's "Measure complexity" stage runs this analyzer over every C# file of
    /// the checkout, tests and migrations included, and grades its maximum (A at 15 or less). This
    /// holds the same file set to the same threshold, so a method that would cost the grade fails
    /// here first. </summary>
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
                && !RepositoryPathFilter.IsNestedCheckout(root, path))
            .Select(path => (path, File.ReadAllText(path)));

        var report = ComplexityAnalyzer.Analyze(sources);
        var details = string.Join(
            Environment.NewLine,
            report.Hotspots.Select(item =>
                $"CC {item.Cyclomatic}: {Path.GetRelativePath(root, item.Path)}:{item.Line} ({item.Member})"));

        Assert.True(
            report.MaxCyclomatic <= ComplexityAnalyzer.HighComplexityThreshold,
            $"Repository max cyclomatic complexity is {report.MaxCyclomatic}; grade A of the nightly quality grade (grade.complexity.maximum) requires <= 15.{Environment.NewLine}{details}");
    }

    private static string Branches(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(index => $"if (n == {index}) return {index}; "));

    [Fact]
    public void AnalyzeDetailed_ListsEveryMethodOverTheThreshold_WithAQualifiedSymbol()
    {
        var src = $$"""
            namespace Shop.Orders;
            public class Checkout
            {
                public int Simple() => 1;
                public int Total(int n) { {{Branches(15)}} return n; }
                public int Total(string text) { {{Branches(15)}} return 0; }
                public int Exactly15(int n) { {{Branches(14)}} return n; }
                public int Name { get { {{Branches(15)}} return 0; } }
            }
            """;

        var analysis = ComplexityAnalyzer.AnalyzeDetailed([("Checkout.cs", src)]);

        // 15 branches + 1 = 16 > 15; 14 + 1 = 15 is not over the threshold.
        Assert.Equal(analysis.Report.HighComplexityMethods, analysis.HighMethods.Count);
        Assert.Equal(
            ["Shop.Orders.Checkout.Total(int)", "Shop.Orders.Checkout.Total(string)", "Shop.Orders.Checkout.Name.get"],
            analysis.HighMethods.Select(item => item.Symbol));
        Assert.All(analysis.HighMethods, item => Assert.Equal(16, item.Cyclomatic));
    }

    [Fact]
    public void AnalyzeDetailed_ASymbolDoesNotDependOnTheLine_AndTwoLambdasOfOneMemberAreTwoMethods()
    {
        var body = $"public int Run(int n) {{ Func<int, int> a = x => {{ {Branches(15).Replace("n ==", "x ==")} return x; }}; "
            + $"Func<int, int> b = x => {{ {Branches(15).Replace("n ==", "x ==")} return x; }}; return a(n) + b(n); }}";
        string Source(string header) => $"{header}namespace Shop; public class Flow {{ {body} }}";

        var first = ComplexityAnalyzer.AnalyzeDetailed([("Flow.cs", Source(string.Empty))]).HighMethods;
        var moved = ComplexityAnalyzer.AnalyzeDetailed([("Flow.cs", Source("using System;\n\n\n"))]).HighMethods;

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Count, first.Select(item => item.Symbol).Distinct().Count());
        Assert.EndsWith("#1", first[0].Symbol, StringComparison.Ordinal);
        Assert.EndsWith("#2", first[1].Symbol, StringComparison.Ordinal);
        Assert.Equal(first.Select(item => item.Symbol), moved.Select(item => item.Symbol));
        Assert.NotEqual(first[0].Line, moved[0].Line);
    }

    [Fact]
    public void Analyze_KeepsReturningTheSameReportAsBefore()
    {
        const string src = "class C { int M(int n) { if (n > 0) return 1; return 0; } }";

        var report = ComplexityAnalyzer.Analyze([("C.cs", src)]);
        var detailed = ComplexityAnalyzer.AnalyzeDetailed([("C.cs", src)]);

        Assert.Equal(report.TotalMethods, detailed.Report.TotalMethods);
        Assert.Equal(report.AvgCyclomatic, detailed.Report.AvgCyclomatic);
        Assert.Equal(report.MaxCyclomatic, detailed.Report.MaxCyclomatic);
        Assert.Equal(report.HighComplexityMethods, detailed.Report.HighComplexityMethods);
        Assert.Equal(report.Hotspots, detailed.Report.Hotspots);
        Assert.Empty(detailed.HighMethods);
    }

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
