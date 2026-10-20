// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

public sealed class ArchitectureAnalyzerTests
{
    [Fact]
    public void Analyze_DetectsCycleAndComputesDsmMetrics()
    {
        var sources = new[]
        {
            ("a.cs", "namespace Product.A; public sealed class A { public Product.B.B Value { get; set; } = new(); }"),
            ("b.cs", "namespace Product.B; public sealed class B { public Product.A.A Value { get; set; } = new(); }")
        };

        var report = ArchitectureAnalyzer.Analyze(sources);

        Assert.Equal(2, report.Edges.Count);
        Assert.Single(report.Cycles);
        Assert.DoesNotContain(report.Violations, violation => violation.RuleId == "architecture-cycle");
        Assert.All(report.Instability.Values, value => Assert.Equal(0.5, value));
        Assert.Equal(4, report.Graphs.Count);
        Assert.All(new[] { "type", "namespace", "assembly", "project" },
            level => Assert.True(report.Graphs.ContainsKey(level)));
        Assert.Equal(2, report.Graphs["type"].Edges.Count);
        Assert.Equal(2, report.Graphs["namespace"].Edges.Count);
        Assert.Empty(report.Graphs["assembly"].Edges);
        Assert.Empty(report.Graphs["project"].Edges);
    }

    [Fact]
    public void Analyze_DistinguishesProjectAndOverriddenAssemblyNames()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-project-identity-").FullName;
        try
        {
            var core = Directory.CreateDirectory(Path.Combine(root, "src", "Product.Core")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(root, "src", "Product.Data")).FullName;
            File.WriteAllText(Path.Combine(core, "Product.Core.csproj"),
                "<Project><PropertyGroup><AssemblyName>Product.Domain</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(data, "Product.Data.csproj"),
                "<Project><PropertyGroup><AssemblyName>Product.Persistence</AssemblyName></PropertyGroup></Project>");
            var sources = new[]
            {
                (Path.Combine(core, "Domain.cs"), "namespace Product.Core; public sealed class Domain { public Product.Data.Store Store { get; set; } = new(); }"),
                (Path.Combine(data, "Store.cs"), "namespace Product.Data; public sealed class Store { }")
            };

            var report = ArchitectureAnalyzer.Analyze(sources);

            var assemblyEdge = Assert.Single(report.Graphs["assembly"].Edges);
            Assert.Equal("Product.Domain", assemblyEdge.Source);
            Assert.Equal("Product.Persistence", assemblyEdge.Target);
            var projectEdge = Assert.Single(report.Graphs["project"].Edges);
            Assert.Equal("Product.Core", projectEdge.Source);
            Assert.Equal("Product.Data", projectEdge.Target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Analyze_AppliesVersionedForbiddenDependencyRules()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-architecture-").FullName;
        try
        {
            var rules = Path.Combine(root, "rules.json");
            File.WriteAllText(rules,
                "{\"schemaVersion\":1,\"forbiddenDependencies\":[{\"source\":\"Core\",\"target\":\"Infrastructure\"}]}");
            var sources = new[]
            {
                ("core.cs", "namespace Core; public sealed class Domain { public Infrastructure.Store Store { get; set; } = new(); }"),
                ("infra.cs", "namespace Infrastructure; public sealed class Store { }")
            };

            var report = ArchitectureAnalyzer.Analyze(sources, rules);

            Assert.Contains(report.Violations, violation => violation.RuleId == "forbidden-dependency"
                && violation.Source == "Core" && violation.Target == "Infrastructure");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Analyze_CapturesImplementationOnlyDependenciesAndReportsEveryGraphCycle()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-architecture-bodies-").FullName;
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root, "src", "Product.First")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(root, "src", "Product.Second")).FullName;
            File.WriteAllText(Path.Combine(first, "Product.First.csproj"),
                "<Project><PropertyGroup><AssemblyName>Product.First.Assembly</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(second, "Product.Second.csproj"),
                "<Project><PropertyGroup><AssemblyName>Product.Second.Assembly</AssemblyName></PropertyGroup></Project>");
            var sources = new[]
            {
                (Path.Combine(first, "First.cs"),
                    "namespace Product.First; public sealed class First { public void Run() { Product.Second.Second.Execute(); } }"),
                (Path.Combine(second, "Second.cs"),
                    "namespace Product.Second; public sealed class Second { public static void Execute() { _ = new Product.First.First(); } }")
            };

            var report = ArchitectureAnalyzer.Analyze(sources);

            Assert.All(new[] { "type", "namespace", "assembly", "project" },
                level => Assert.Single(report.Graphs[level].Cycles));
            Assert.DoesNotContain(report.Violations, item => item.RuleId == "architecture-cycle");
            Assert.DoesNotContain(report.Violations, item => item.RuleId == "architecture-cycle-type");
            Assert.Contains(report.Violations, item => item.RuleId == "architecture-cycle-assembly");
            Assert.Contains(report.Violations, item => item.RuleId == "architecture-cycle-project");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Analyze_ReportsAnActualDirectedCyclePath()
    {
        var sources = new[]
        {
            ("a.cs", "namespace Graph.A; public sealed class A { public Graph.C.C Value { get; set; } = new(); }"),
            ("b.cs", "namespace Graph.B; public sealed class B { public Graph.A.A Value { get; set; } = new(); }"),
            ("c.cs", "namespace Graph.C; public sealed class C { public Graph.B.B Value { get; set; } = new(); }")
        };

        var report = ArchitectureAnalyzer.Analyze(sources);
        var cycle = Assert.Single(report.Cycles);
        var edges = report.Edges.Select(item => (item.Source, item.Target)).ToHashSet();

        for (var index = 0; index < cycle.Count; index++)
            Assert.Contains((cycle[index], cycle[(index + 1) % cycle.Count]), edges);
    }

    [Fact]
    public void Analyze_WithoutAProjectFile_InfersTheProjectFromTheFolders()
    {
        // Folders that do not exist: no .csproj can be found above them.
        var root = Path.Combine(Path.GetTempPath(), $"aetheus-no-project-{Guid.NewGuid():N}");
        var sources = new[]
        {
            (Path.Combine(root, "src", "Product.Core", "Domain.cs"),
                "namespace Product.Core; public sealed class Domain { public Product.Store.Store Store { get; set; } = new(); public Product.Other.Other Other { get; set; } = new(); }"),
            (Path.Combine(root, "src", "Product.Core", "Second.cs"),
                "namespace Product.Core; public sealed class Second { public Product.Store.Store Store { get; set; } = new(); }"),
            (Path.Combine(root, "lib", "Alpha", "Store.cs"), "namespace Product.Store; public sealed class Store { }"),
            (Path.Combine(root, "tests", "Beta", "Other.cs"), "namespace Product.Other; public sealed class Other { }")
        };

        var report = ArchitectureAnalyzer.Analyze(sources);

        var projectEdges = report.Graphs["project"].Edges.Select(edge => (edge.Source, edge.Target)).ToHashSet();
        Assert.Equal([("Product.Core", "Alpha"), ("Product.Core", "Beta")], projectEdges.Order());
        var assemblyEdges = report.Graphs["assembly"].Edges.Select(edge => (edge.Source, edge.Target)).ToHashSet();
        Assert.Equal(projectEdges, assemblyEdges);
    }

    [Fact]
    public void RepositoryProductGraph_HasNoUnacceptedArchitectureViolations()
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

        var report = ArchitectureAnalyzer.Analyze(
            productSources,
            Path.Combine(root, ".aetheus", "architecture-rules.json"));
        var details = string.Join(
            Environment.NewLine,
            report.Violations.Select(item => $"{item.RuleId}: {item.Source} -> {item.Target}: {item.Message}"));

        Assert.True(report.Violations.Count == 0, details);
    }

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
