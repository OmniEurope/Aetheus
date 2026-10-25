// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Immutable;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aetheus.Agent.Core.Operations;

/// <param name="NavigationCount">How many of the <paramref name="Count"/> references are EF Core navigation properties
/// between two entity types (ADR-056): an edge whose references are all navigations does not make a cycle.</param>
public sealed record ArchitectureEdge(string Source, string Target, int Count, int NavigationCount = 0);
/// <param name="Identity">What makes the violation the same one from a run to the next, when its message
/// cannot: a cycle group is identified by its graph and its smallest member, while the path the message gives
/// as an example can change with the code. Null keeps the message as the identity.</param>
public sealed record ArchitectureViolation(
    string RuleId, string Source, string Target, string Message, string? Identity = null);

/// <param name="CycleGroups">The members of each group of cycles (a strongly connected component), sorted,
/// in the order of <paramref name="Cycles"/>: <c>Cycles[i]</c> is one concrete cycle of <c>CycleGroups[i]</c>.</param>
public sealed record ArchitectureGraph(
    IReadOnlyList<ArchitectureEdge> Edges,
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    IReadOnlyDictionary<string, double> Instability,
    IReadOnlyList<IReadOnlyList<string>> CycleGroups);
public sealed record ArchitectureReport(
    IReadOnlyList<ArchitectureEdge> Edges,
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    IReadOnlyList<ArchitectureViolation> Violations,
    IReadOnlyDictionary<string, double> Instability,
    IReadOnlyDictionary<string, ArchitectureGraph> Graphs);

public static class ArchitectureAnalyzer
{
    public static ArchitectureReport Analyze(
        IEnumerable<(string Path, string Content)> files,
        string? rulesPath = null)
        => Analyze(
            files.Select(file => new ParsedCSharpSource(
                file.Path,
                file.Content,
                CSharpSyntaxTree.ParseText(file.Content, path: file.Path))),
            rulesPath);

    public static ArchitectureReport Analyze(
        IEnumerable<ParsedCSharpSource> files,
        string? rulesPath = null)
    {
        var sourceFiles = files.ToList();
        var trees = sourceFiles.Select(file => file.SyntaxTree).ToList();
        var references = TrustedPlatformReferences();
        var compilation = CSharpCompilation.Create("AetheusArchitectureAnalysis", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var counts = new Dictionary<string, Dictionary<(string Source, string Target), int>>(StringComparer.Ordinal)
        {
            ["type"] = [],
            ["namespace"] = [],
            ["assembly"] = [],
            ["project"] = []
        };
        // The part of each edge that comes from EF Core navigation properties between two entity types (ADR-056).
        var navigationCounts = counts.ToDictionary(
            item => item.Key, _ => new Dictionary<(string Source, string Target), int>(), StringComparer.Ordinal);
        var entities = EfEntityModel.Build(compilation, trees);
        var visitor = new DependencySymbolVisitor(counts, navigationCounts, entities);
        visitor.Visit(compilation.Assembly.GlobalNamespace);
        foreach (var tree in trees)
        {
            var semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            new DependencySyntaxWalker(semanticModel, visitor, entities).Visit(tree.GetRoot());
        }

        var graphs = counts.ToDictionary(
            item => item.Key,
            item => BuildGraph(item.Value, navigationCounts[item.Key]),
            StringComparer.Ordinal);
        var namespaceGraph = graphs["namespace"];
        var edges = namespaceGraph.Edges;
        var cycles = namespaceGraph.Cycles;
        var violations = FindViolations(edges, rulesPath);
        // ADR-056: every group of cycles is a finding, whatever the graph. The type and namespace graphs, whose
        // cycles used to be a count against a threshold, name the members of the group; the assembly and
        // project graphs keep the message their findings already had, so those stay the same findings.
        foreach (var graph in graphs)
            for (var index = 0; index < graph.Value.Cycles.Count; index++)
                violations.Add(CycleViolation(graph.Key, graph.Value.Cycles[index], graph.Value.CycleGroups[index]));
        return new ArchitectureReport(edges, cycles, violations, namespaceGraph.Instability, graphs);
    }

    /// <summary>How many members of a group the message of its finding lists before saying how many more.</summary>
    private const int MaxListedCycleMembers = 30;

    private static ArchitectureViolation CycleViolation(
        string graph, IReadOnlyList<string> cycle, IReadOnlyList<string> members)
    {
        var ruleId = graph == "namespace" ? "architecture-cycle" : $"architecture-cycle-{graph}";
        var path = $"{string.Join(" -> ", cycle)} -> {cycle[0]}";
        if (graph is not ("type" or "namespace"))
            return new ArchitectureViolation(ruleId, cycle[0], cycle[^1],
                $"{char.ToUpperInvariant(graph[0])}{graph[1..]} dependency cycle: {path}");

        var listed = string.Join(", ", members.Take(MaxListedCycleMembers));
        var more = members.Count > MaxListedCycleMembers ? $" and {members.Count - MaxListedCycleMembers} more" : string.Empty;
        // The group is named by its smallest member, which is also where the example path starts.
        return new ArchitectureViolation(ruleId, cycle[0], cycle[^1],
            $"{char.ToUpperInvariant(graph[0])}{graph[1..]} dependency cycle between {members.Count} {graph}s: {listed}{more}. "
            + $"One path: {path}",
            $"{graph}:{members[0]}");
    }

    private static ArchitectureGraph BuildGraph(
        Dictionary<(string Source, string Target), int> counts,
        Dictionary<(string Source, string Target), int> navigationCounts)
    {
        var edges = counts
            .Where(item => !string.Equals(item.Key.Source, item.Key.Target, StringComparison.Ordinal))
            .Select(item => new ArchitectureEdge(
                item.Key.Source, item.Key.Target, item.Value, navigationCounts.GetValueOrDefault(item.Key)))
            .OrderBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Target, StringComparer.Ordinal)
            .ToList();
        // An edge is a navigation edge when every reference behind it is a navigation property between entity types.
        var navigationEdges = edges
            .Where(edge => edge.NavigationCount == edge.Count)
            .GroupBy(edge => edge.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.Target).ToList(), StringComparer.Ordinal);
        var (cycles, groups) = FindCycles(edges, navigationEdges);
        return new ArchitectureGraph(edges, cycles, CalculateInstability(edges), groups);
    }

    private static ImmutableArray<MetadataReference> TrustedPlatformReferences()
    {
        var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator) ?? [];
        return paths.Select(path => MetadataReference.CreateFromFile(path)).ToImmutableArray<MetadataReference>();
    }

    private static List<ArchitectureViolation> FindViolations(
        IReadOnlyList<ArchitectureEdge> edges,
        string? rulesPath)
    {
        if (string.IsNullOrWhiteSpace(rulesPath) || !File.Exists(rulesPath)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(rulesPath), new JsonDocumentOptions { MaxDepth = 16 });
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1
            || !document.RootElement.TryGetProperty("forbiddenDependencies", out var rules)
            || rules.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ArchitectureViolation>();
        foreach (var rule in rules.EnumerateArray())
        {
            var source = rule.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString() : null;
            var target = rule.TryGetProperty("target", out var targetValue) ? targetValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) continue;
            foreach (var edge in edges.Where(edge => Matches(edge.Source, source) && Matches(edge.Target, target)))
                result.Add(new ArchitectureViolation("forbidden-dependency", edge.Source, edge.Target,
                    $"Forbidden namespace dependency: {edge.Source} -> {edge.Target}."));
        }
        return result;
    }

    private static bool Matches(string value, string prefix) =>
        string.Equals(value, prefix, StringComparison.Ordinal)
        || value.StartsWith(prefix + ".", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, double> CalculateInstability(IReadOnlyList<ArchitectureEdge> edges)
    {
        var nodes = edges.SelectMany(edge => new[] { edge.Source, edge.Target }).Distinct(StringComparer.Ordinal);
        return nodes.ToDictionary(node => node, node =>
        {
            var fanOut = edges.Count(edge => edge.Source == node);
            var fanIn = edges.Count(edge => edge.Target == node);
            return fanIn + fanOut == 0 ? 0 : Math.Round((double)fanOut / (fanIn + fanOut), 4);
        }, StringComparer.Ordinal);
    }

    private static (List<IReadOnlyList<string>> Cycles, List<IReadOnlyList<string>> Groups) FindCycles(
        IReadOnlyList<ArchitectureEdge> edges,
        IReadOnlyDictionary<string, List<string>> navigationEdges)
    {
        var adjacency = edges.GroupBy(edge => edge.Source)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.Target).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        var index = 0;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var cycles = new List<IReadOnlyList<string>>();
        var groups = new List<IReadOnlyList<string>>();
        foreach (var node in edges.SelectMany(edge => new[] { edge.Source, edge.Target }).Distinct(StringComparer.Ordinal))
            if (!indexes.ContainsKey(node)) Visit(node);
        return (cycles, groups);

        void Visit(string node)
        {
            indexes[node] = index;
            lowLinks[node] = index++;
            stack.Push(node);
            onStack.Add(node);
            foreach (var target in adjacency.GetValueOrDefault(node) ?? [])
            {
                if (!indexes.ContainsKey(target))
                {
                    Visit(target);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
                }
                else if (onStack.Contains(target)) lowLinks[node] = Math.Min(lowLinks[node], indexes[target]);
            }
            if (lowLinks[node] != indexes[node]) return;
            var component = new List<string>();
            string current;
            do
            {
                current = stack.Pop();
                onStack.Remove(current);
                component.Add(current);
            } while (!string.Equals(current, node, StringComparison.Ordinal));
            if (component.Count > 1 && !IsExplainedByNavigations(component, navigationEdges))
            {
                var cycle = FindConcreteCycle(component, adjacency);
                if (cycle.Count <= 1) return;
                cycles.Add(cycle);
                groups.Add([.. component.Order(StringComparer.Ordinal)]);
            }
        }
    }

    /// <summary>
    /// ADR-056: a group of cycles is standard EF Core bidirectional navigation, not a design defect, when its
    /// members are strongly connected by navigation edges alone. One reference that is not a navigation between
    /// entity types (a service, a method parameter, a non-entity type) keeps the whole group a finding.
    /// </summary>
    private static bool IsExplainedByNavigations(
        List<string> component,
        IReadOnlyDictionary<string, List<string>> navigationEdges)
    {
        var members = component.ToHashSet(StringComparer.Ordinal);
        var forward = Reach(component[0], navigationEdges, members);
        if (forward.Count != members.Count) return false;
        var reversed = navigationEdges
            .SelectMany(item => item.Value.Select(target => (Source: target, Target: item.Key)))
            .GroupBy(edge => edge.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.Target).ToList(), StringComparer.Ordinal);
        return Reach(component[0], reversed, members).Count == members.Count;
    }

    private static HashSet<string> Reach(
        string start,
        IReadOnlyDictionary<string, List<string>> edges,
        HashSet<string> members)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var pending = new Stack<string>([start]);
        while (pending.Count > 0)
            foreach (var target in edges.GetValueOrDefault(pending.Pop()) ?? [])
                if (members.Contains(target) && seen.Add(target)) pending.Push(target);
        return seen;
    }

    private static IReadOnlyList<string> FindConcreteCycle(
        IReadOnlyCollection<string> component,
        IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var allowed = component.ToHashSet(StringComparer.Ordinal);
        foreach (var start in component.Order(StringComparer.Ordinal))
        {
            var path = new List<string> { start };
            var inPath = new HashSet<string>(StringComparer.Ordinal) { start };
            if (Search(start)) return path;

            bool Search(string current)
            {
                foreach (var target in adjacency.GetValueOrDefault(current) ?? [])
                {
                    if (!allowed.Contains(target)) continue;
                    if (string.Equals(target, start, StringComparison.Ordinal) && path.Count > 1)
                        return true;
                    if (!inPath.Add(target)) continue;
                    path.Add(target);
                    if (Search(target)) return true;
                    path.RemoveAt(path.Count - 1);
                    inPath.Remove(target);
                }
                return false;
            }
        }
        return [];
    }

    private sealed class DependencySyntaxWalker(
        SemanticModel semanticModel,
        DependencySymbolVisitor dependencies,
        EfEntityModel entities) : CSharpSyntaxWalker
    {
        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            Add(node);
            base.VisitIdentifierName(node);
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            Add(node);
            base.VisitGenericName(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            Add(node);
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            Add(node);
            base.VisitImplicitObjectCreationExpression(node);
        }

        private void Add(SyntaxNode node)
        {
            var source = semanticModel.GetEnclosingSymbol(node.SpanStart)?.ContainingType;
            if (source is null) return;
            var symbolInfo = semanticModel.GetSymbolInfo(node);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            var target = symbol switch
            {
                INamedTypeSymbol namedType => namedType,
                IMethodSymbol method => method.ContainingType,
                IPropertySymbol property => property.ContainingType,
                IFieldSymbol field => field.ContainingType,
                IEventSymbol eventSymbol => eventSymbol.ContainingType,
                _ => semanticModel.GetTypeInfo(node).Type
            };
            dependencies.Add(source, target, entities.IsInNavigationDeclaration(semanticModel, node));
        }
    }

    private sealed class DependencySymbolVisitor(
        IReadOnlyDictionary<string, Dictionary<(string Source, string Target), int>> graphs,
        IReadOnlyDictionary<string, Dictionary<(string Source, string Target), int>> navigationGraphs,
        EfEntityModel entities) : SymbolVisitor
    {
        private readonly Dictionary<string, ProjectIdentity> projectIdentities = new(StringComparer.OrdinalIgnoreCase);

        public override void VisitNamespace(INamespaceSymbol symbol)
        {
            foreach (var member in symbol.GetMembers()) member.Accept(this);
        }

        public override void VisitNamedType(INamedTypeSymbol symbol)
        {
            Add(symbol, symbol.BaseType);
            foreach (var item in symbol.Interfaces) Add(symbol, item);
            foreach (var member in symbol.GetMembers())
            {
                switch (member)
                {
                    case IFieldSymbol field: Add(symbol, field.Type, IsNavigationAccessor(field.AssociatedSymbol)); break;
                    case IPropertySymbol property: Add(symbol, property.Type, entities.NavigationTarget(property) is not null); break;
                    case IEventSymbol eventSymbol: Add(symbol, eventSymbol.Type); break;
                    case IMethodSymbol method:
                        var navigation = IsNavigationAccessor(method.AssociatedSymbol);
                        Add(symbol, method.ReturnType, navigation);
                        foreach (var parameter in method.Parameters) Add(symbol, parameter.Type, navigation);
                        break;
                    case INamedTypeSymbol nested: nested.Accept(this); break;
                }
            }
        }

        // The backing field and the accessors of a navigation property are the navigation itself.
        private bool IsNavigationAccessor(ISymbol? associated) =>
            associated is IPropertySymbol property && entities.NavigationTarget(property) is not null;

        /// <param name="navigation">The reference is the type or the initializer of a navigation property of
        /// <paramref name="source"/>; it counts as a navigation edge only when both ends are entity types.</param>
        internal void Add(INamedTypeSymbol source, ITypeSymbol? type, bool navigation = false)
        {
            if (type is null) return;
            if (type is IArrayTypeSymbol array) type = array.ElementType;
            if (type is INamedTypeSymbol named)
            {
                foreach (var argument in named.TypeArguments) Add(source, argument, navigation);
                if (Namespace(named).StartsWith("System", StringComparison.Ordinal)) return;
                var isNavigation = navigation && entities.IsEntity(source) && entities.IsEntity(named);
                AddEdge("type", TypeName(source), TypeName(named), isNavigation);
                AddEdge("namespace", Namespace(source), Namespace(named), isNavigation);
                AddEdge("assembly", Identity(source).Assembly, Identity(named).Assembly, isNavigation);
                AddEdge("project", Identity(source).Project, Identity(named).Project, isNavigation);
            }
        }

        private void AddEdge(string level, string source, string target, bool navigation)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)
                || source == target || target.StartsWith("System", StringComparison.Ordinal)) return;
            var edges = graphs[level];
            edges[(source, target)] = edges.GetValueOrDefault((source, target)) + 1;
            if (!navigation) return;
            var navigationEdges = navigationGraphs[level];
            navigationEdges[(source, target)] = navigationEdges.GetValueOrDefault((source, target)) + 1;
        }

        private static string TypeName(INamedTypeSymbol symbol) =>
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

        private static string Namespace(ISymbol symbol) =>
            symbol.ContainingNamespace is null || symbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : symbol.ContainingNamespace.ToDisplayString();

        private ProjectIdentity Identity(INamedTypeSymbol symbol)
        {
            var path = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                var fallback = symbol.ContainingAssembly?.Name ?? string.Empty;
                return new ProjectIdentity(fallback, fallback);
            }
            var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
            if (projectIdentities.TryGetValue(directory, out var cached)) return cached;
            var projectFile = FindProjectFile(path);
            return projectIdentities[directory] = projectFile is not null
                ? ProjectFileIdentity(projectFile)
                : InferIdentity(path, symbol.ContainingAssembly?.Name);
        }

        // The project named by its .csproj, and the assembly it builds (its AssemblyName when set).
        private ProjectIdentity ProjectFileIdentity(string projectFile)
        {
            if (projectIdentities.TryGetValue(projectFile, out var cached)) return cached;
            var project = Path.GetFileNameWithoutExtension(projectFile);
            var assembly = ReadAssemblyName(projectFile) ?? project;
            return projectIdentities[projectFile] = new ProjectIdentity(project, assembly);
        }

        // No project file: the folder under src/ or tests/, else the file's own folder.
        private static ProjectIdentity InferIdentity(string path, string? assemblyName)
        {
            var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length - 1; index++)
            {
                if (segments[index] is "src" or "tests")
                    return new ProjectIdentity(segments[index + 1], segments[index + 1]);
            }
            var name = segments.Length > 1 ? segments[^2] : assemblyName ?? string.Empty;
            return new ProjectIdentity(name, name);
        }

        private static string? FindProjectFile(string sourcePath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            for (var depth = 0; depth < 12 && directory is not null; depth++)
            {
                string? project = null;
                try
                {
                    if (Directory.Exists(directory))
                        project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
                if (project is not null) return project;
                if (File.Exists(Path.Combine(directory, "Aetheus.slnx"))) return null;
                directory = Path.GetDirectoryName(directory);
            }
            return null;
        }

        private static string? ReadAssemblyName(string projectFile)
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(projectFile, settings);
                return XDocument.Load(reader).Descendants("AssemblyName")
                    .Select(element => element.Value.Trim())
                    .FirstOrDefault(value => value.Length > 0);
            }
            catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private sealed record ProjectIdentity(string Project, string Assembly);
    }
}
