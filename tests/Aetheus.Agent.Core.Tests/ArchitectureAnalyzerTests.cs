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
        // R1-36 / ADR-056: a group of cycles is a finding, whatever the graph, naming its members.
        var violation = Assert.Single(report.Violations, item => item.RuleId == "architecture-cycle");
        Assert.Equal("namespace:Product.A", violation.Identity);
        Assert.Contains("Product.A, Product.B", violation.Message, StringComparison.Ordinal);
        Assert.Equal(["Product.A", "Product.B"], report.Graphs["namespace"].CycleGroups.Single());
        Assert.Single(report.Violations, item => item.RuleId == "architecture-cycle-type");
        Assert.All(report.Instability.Values, value => Assert.Equal(0.5, value));
        Assert.Equal(4, report.Graphs.Count);
        Assert.All(new[] { "type", "namespace", "assembly", "project" },
            level => Assert.True(report.Graphs.ContainsKey(level)));
        Assert.Equal(2, report.Graphs["type"].Edges.Count);
        Assert.Equal(2, report.Graphs["namespace"].Edges.Count);
        Assert.Empty(report.Graphs["assembly"].Edges);
        Assert.Empty(report.Graphs["project"].Edges);
    }

    private const string EfEntities = """
        namespace Product.Servers { public sealed class Server { public Guid Id { get; set; } public ICollection<Product.Pipelines.Pipeline> Pipelines { get; set; } = []; } }
        namespace Product.Pipelines { public sealed class Pipeline { public Guid ServerId { get; set; } public Product.Servers.Server Server { get; set; } = null!; } }
        """;

    private static IEnumerable<ArchitectureViolation> Cycles(ArchitectureReport report) =>
        report.Violations.Where(item => item.RuleId.StartsWith("architecture-cycle", StringComparison.Ordinal));

    [Fact]
    public void Analyze_IgnoresTheBidirectionalNavigationOfTwoDbSetEntities()
    {
        var sources = new[]
        {
            ("entities.cs", "using System; using System.Collections.Generic;\n" + EfEntities),
            ("context.cs", "namespace Product.Data; public class AppDbContext { public DbSet<Product.Servers.Server> Servers => null!; public DbSet<Product.Pipelines.Pipeline> Pipelines => null!; }")
        };

        var report = ArchitectureAnalyzer.Analyze(sources);

        Assert.Empty(Cycles(report));
        Assert.Empty(report.Cycles);
        // The dependency itself is still in the graph: only the cycle it forms is not a finding.
        Assert.Contains(report.Graphs["type"].Edges, edge => edge.Source == "Product.Servers.Server" && edge.Target == "Product.Pipelines.Pipeline");
        Assert.Contains(report.Graphs["type"].Edges, edge => edge.Source == "Product.Pipelines.Pipeline" && edge.Target == "Product.Servers.Server");
    }

    [Fact]
    public void Analyze_RecognisesEntitiesDeclaredByAConfigurationOrTheModelBuilder()
    {
        var sources = new[]
        {
            ("entities.cs", "using System; using System.Collections.Generic;\n" + EfEntities),
            ("configuration.cs", "namespace Product.Data; public sealed class ServerConfiguration : IEntityTypeConfiguration<Product.Servers.Server> { }"),
            ("model.cs", "namespace Product.Data; public sealed class Model { void Build(ModelBuilder modelBuilder) { modelBuilder.Entity<Product.Pipelines.Pipeline>(); } }")
        };

        Assert.Empty(Cycles(ArchitectureAnalyzer.Analyze(sources)));
    }

    [Fact]
    public void Analyze_StillReportsTheSameMutualReferenceWhenTheTypesAreNotEntities()
    {
        var sources = new[] { ("entities.cs", "using System; using System.Collections.Generic;\n" + EfEntities) };

        var report = ArchitectureAnalyzer.Analyze(sources);

        Assert.Contains(report.Violations, item => item.RuleId == "architecture-cycle-type");
        Assert.Contains(report.Violations, item => item.RuleId == "architecture-cycle");
    }

    [Fact]
    public void Analyze_StillReportsACycleBetweenAnEntityAndAService()
    {
        var sources = new[]
        {
            ("entities.cs", """
                using System; using System.Collections.Generic;
                namespace Product.Servers { public sealed class Server { public ICollection<Product.Pipelines.Pipeline> Pipelines { get; set; } = []; } }
                namespace Product.Pipelines { public sealed class Pipeline { public Product.Servers.Server Server { get; set; } = null!; public Product.Services.PipelineService Starter { get; set; } = null!; } }
                """),
            ("context.cs", "namespace Product.Data; public class AppDbContext { public DbSet<Product.Servers.Server> Servers => null!; public DbSet<Product.Pipelines.Pipeline> Pipelines => null!; }"),
            ("service.cs", "namespace Product.Services; public sealed class PipelineService { public void Start(Product.Pipelines.Pipeline pipeline) { } }")
        };

        var report = ArchitectureAnalyzer.Analyze(sources);

        var violation = Assert.Single(report.Violations, item => item.RuleId == "architecture-cycle-type");
        Assert.Contains("PipelineService", violation.Message, StringComparison.Ordinal);
        Assert.Contains("Product.Pipelines.Pipeline", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_StillReportsTwoEntitiesThatReferenceEachOtherOutsideTheirNavigations()
    {
        var sources = new[]
        {
            ("entities.cs", """
                using System; using System.Collections.Generic;
                namespace Product.Servers { public sealed class Server { public ICollection<Product.Pipelines.Pipeline> Pipelines { get; set; } = []; public void Attach(Product.Pipelines.Pipeline pipeline) { } } }
                namespace Product.Pipelines { public sealed class Pipeline { public Product.Servers.Server Server { get; set; } = null!; } }
                """),
            ("context.cs", "namespace Product.Data; public class AppDbContext { public DbSet<Product.Servers.Server> Servers => null!; public DbSet<Product.Pipelines.Pipeline> Pipelines => null!; }")
        };

        var report = ArchitectureAnalyzer.Analyze(sources);

        Assert.Contains(report.Violations, item => item.RuleId == "architecture-cycle-type");
    }

    [Fact]
    public void Analyze_StillReportsACycleBetweenAnEntityAndANonEntityType()
    {
        var sources = new[]
        {
            ("entities.cs", """
                namespace Product.Servers { public sealed class Server { public Product.Values.Settings Settings { get; set; } = null!; } }
                namespace Product.Values { public sealed class Settings { public Product.Servers.Server Owner { get; set; } = null!; } }
                """),
            ("context.cs", "namespace Product.Data; public class AppDbContext { public DbSet<Product.Servers.Server> Servers => null!; }")
        };

        var report = ArchitectureAnalyzer.Analyze(sources);

        Assert.Contains(report.Violations, item => item.RuleId == "architecture-cycle-type");
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
            Assert.Contains(report.Violations, item => item.RuleId == "architecture-cycle");
            Assert.Contains(report.Violations, item => item.RuleId == "architecture-cycle-type");
            Assert.Contains(report.Violations, item => item.RuleId == "architecture-cycle-assembly");
            Assert.Contains(report.Violations, item => item.RuleId == "architecture-cycle-project");
            // The assembly and project findings keep the identity they always had (their message); the type
            // and namespace ones are identified by their group.
            Assert.All(report.Violations.Where(item => item.RuleId is "architecture-cycle-assembly" or "architecture-cycle-project"),
                item => Assert.Null(item.Identity));
            Assert.All(report.Violations.Where(item => item.RuleId is "architecture-cycle" or "architecture-cycle-type"),
                item => Assert.NotNull(item.Identity));
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
