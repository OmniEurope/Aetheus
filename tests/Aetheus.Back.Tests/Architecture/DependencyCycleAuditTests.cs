// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Forbids circular dependencies in <c>Components/</c>, at two granularities:
/// <list type="bullet">
///   <item>Class level: no constructor-injection cycle between concrete Components types.
///   Runtime DI would throw on a plain cycle, but a cycle hidden behind <c>Lazy&lt;T&gt;</c> or
///   <c>Func&lt;T&gt;</c> resolves fine and rots silently - this guard unwraps those too.</item>
///   <item>Module level: no cycle between <c>Components/{Module}</c> namespaces. Cross-module
///   dependencies are allowed (interfaces only, per AGENTS.md) but must stay acyclic, so a
///   module graph A -&gt; B -&gt; A fails the build instead of ossifying into a god knot.</item>
/// </list>
/// Break a cycle with a callback interface passed as a parameter, or by extracting the shared
/// piece into its own collaborator - never with a lazy resolver hack. Per AGENTS.md, adding a
/// whitelist entry here requires explicit user approval in the session that adds it.
/// </summary>
public class DependencyCycleAuditTests
{
    private static readonly Assembly BackAssembly = typeof(Program).Assembly;

    /// <summary>
    /// Known class-level cycles tolerated pending an approved refactor. Keep empty.
    /// Entries are "TypeA -> TypeB -> TypeA" chains and require explicit user approval.
    /// </summary>
    private static readonly HashSet<string> ClassCycleWhitelist = [];

    /// <summary>
    /// The ratchet baseline, kept at EMPTY. It began at 33 frozen cyclic edges on 2026-08-16 and was
    /// emptied slice by slice; the layer map below is what replaces it. Adding an entry requires
    /// explicit user approval in the session that adds it, per AGENTS.md.
    /// </summary>
    private static readonly HashSet<string> FrozenCyclicModuleEdges =
    [
    ];

    /// <summary>
    /// Which layer each <c>Components/</c> module sits in. Dependencies run strictly downward, so a
    /// cycle is impossible by construction rather than merely absent today - that is the whole point
    /// of replacing the frozen baseline with this map.
    ///
    /// Layer 0 is pure storage that depends on no other module; each layer above may only reach
    /// layers strictly below it. The placement is not arbitrary: it follows the direction each
    /// concern actually runs. Tasks sits below Pipelines because "a step finished" is a notification
    /// from the executor upward, not a command downward. Notifications sits low because reacting to
    /// an event is the reactor's business. Artifacts sits below Releases because a release names its
    /// artifacts, not the other way round.
    ///
    /// **Changing this map requires explicit user approval in the session that changes it.** Moving a
    /// module up a layer is how a cycle would be re-legalised, and it is exactly the move a failing
    /// build tempts you into. The fix for a violation is to invert the edge - a domain event, an
    /// own-read over shared entities, or moving the code to the module that owns the concern.
    /// </summary>
    private static readonly Dictionary<string, int> ModuleLayers = new(StringComparer.Ordinal)
    {
        // L-1 - the shared helpers every module uses (grid query map, SSRF guard, safe XML, mappers of
        // shared entities). It reaches no module at all: it sat at L5 only because it registered two
        // modules' event handlers and located a server repository, both moved out on 2026-09-25, which
        // had left twenty lower modules pointing up at it.
        ["Shared"] = -1,

        // L0 - storage that reaches nothing.
        // Security holds the CSP violation collector: it logs what the browser refused and touches
        // no other module, so it belongs at the bottom where it can gain no dependency by accident.
        ["Security"] = 0,
        ["Audit"] = 0,
        ["GitGraph"] = 0,
        ["Artifacts"] = 0,

        // L1 - foundations: identity, configuration, delivery channels.
        ["AgentPools"] = 1,
        ["Dashboards"] = 1,
        ["Environments"] = 1,
        ["Logs"] = 1,
        ["Notifications"] = 1,
        ["Organizations"] = 1,
        ["PackageFeeds"] = 1,
        ["ServerApps"] = 1,
        ["ServerModules"] = 1,
        // Read models and per-server links: both reach no other module today, and sitting low is
        // what keeps it that way - the next time one needs a run or a server it must read the shared
        // entity itself rather than injecting the module that owns it.
        ["ModuleLinks"] = 1,
        ["Monitoring"] = 1,
        // Who holds which port on which server. Storage only - it reaches no other module, and the
        // orchestrator above reads it through IPortRegistryService.
        ["PortRegistry"] = 1,
        ["ServiceConnections"] = 1,
        ["Settings"] = 1,
        ["Users"] = 1,
        ["VariableLibraries"] = 1,
        ["Vaults"] = 1,
        ["Webhooks"] = 1,
        ["WorkItems"] = 1,

        // L2 - execution and access primitives.
        // Alerts sends through Notifications (L1): an alert is a producer of notifications, so it sits
        // above the channel it uses (raised from L1 on 2026-09-25, when the source guard found the call;
        // nothing depends on Alerts, so no cycle can form).
        ["Alerts"] = 2,
        ["AppMonitoring"] = 2,
        // This coordinator sits above PortRegistry and VariableLibraries to own allocation plus
        // variable writing without coupling those peer modules.
        ["PortAllocation"] = 2,
        ["Auth"] = 2,
        ["Plugins"] = 2,
        ["SystemLogs"] = 2,
        ["Tasks"] = 2,

        // L3 - things driven onto a machine through tasks.
        ["AgentInstaller"] = 3,
        ["AgentUpdate"] = 3,
        ["Apache"] = 3,
        ["Certbot"] = 3,
        ["Cron"] = 3,
        ["Docker"] = 3,
        ["PersonalAccessTokens"] = 3,
        ["Portsentry"] = 3,
        ["Rkhunter"] = 3,
        ["ServerConfigurations"] = 3,

        // L4 - the machines and the repositories themselves.
        ["Git"] = 4,
        ["PackageRegistry"] = 4,
        ["Servers"] = 4,
        // The daily recap reads servers, backups, certificates and agent updates; nothing depends on it
        // (Settings spells its key prefix as text). Approved by the user on 2026-10-10.
        ["DailyRecap"] = 4,

        // L5 - services that act on machines and repositories.
        ["AiTasks"] = 5,
        ["AppBackups"] = 5,
        ["Mail"] = 5,
        ["Teamspeak"] = 5,

        // L6 - the orchestrator.
        ["Pipelines"] = 6,

        // L7 - what reads a run's output.
        ["Analysis"] = 7,
        ["TestManagement"] = 7,

        // L8 - what owns pipelines.
        ["Projects"] = 8,

        // L9 - what a project ships.
        ["ExternalRepos"] = 9,
        ["Releases"] = 9,

        // L10 - developer-only tooling. It sits at the top because it is allowed to reach anything
        // for seeding and diagnostics, and nothing may depend on it: a production module that needed
        // something from here would be reaching into a controller that does not ship behaviour.
        ["Dev"] = 10
    };

    [Fact]
    public void Components_Classes_Have_No_Constructor_Injection_Cycles()
    {
        var (nodes, edges) = BuildClassGraph();
        Assert.True(nodes.Count > 30, $"Expected many Components types, found {nodes.Count}");

        var cycles = FindCycles(nodes, edges)
            .Where(c => !ClassCycleWhitelist.Contains(c))
            .ToList();

        Assert.True(cycles.Count == 0,
            "Constructor-injection cycles detected between Components classes "
            + "(break with a callback interface passed as a parameter, or extract the shared piece):\n  "
            + string.Join("\n  ", cycles));
    }

    [Fact]
    public void Components_Modules_Have_No_Dependency_Cycles()
    {
        var (nodes, edges) = BuildClassGraph();
        var moduleEdges = new Dictionary<string, HashSet<string>>();
        foreach (var (from, targets) in edges)
        {
            var fromModule = ModuleOf(from);
            if (fromModule is null) continue;
            foreach (var to in targets)
            {
                var toModule = ModuleOf(to);
                if (toModule is null || toModule == fromModule) continue;
                if (!moduleEdges.TryGetValue(fromModule, out var set))
                {
                    set = [];
                    moduleEdges[fromModule] = set;
                }
                set.Add(toModule);
            }
        }

        var modules = nodes.Select(ModuleOf).Where(m => m is not null).Cast<string>().Distinct().ToList();
        Assert.True(modules.Count > 5, $"Expected several Components modules, found {modules.Count}");

        // Ratchet: an edge is cyclic when both ends sit in the same strongly-connected component.
        // Every cyclic edge must be in the frozen baseline; a NEW cyclic edge fails the build, and
        // a frozen edge that no longer cycles must be removed from the baseline (list only shrinks).
        var cyclicEdges = FindCyclicEdges(modules, moduleEdges);
        var newEdges = cyclicEdges.Where(e => !FrozenCyclicModuleEdges.Contains(e)).ToList();
        var staleEntries = FrozenCyclicModuleEdges.Where(e => !cyclicEdges.Contains(e)).Order().ToList();

        Assert.True(newEdges.Count == 0,
            "NEW dependency cycles between Components modules "
            + "(cross-module deps must stay acyclic; invert one edge via a callback interface "
            + "instead of freezing it):\n  " + string.Join("\n  ", newEdges));
        Assert.True(staleEntries.Count == 0,
            "Frozen cyclic edges that no longer cycle - remove them from the baseline so the "
            + "ratchet keeps tightening:\n  " + string.Join("\n  ", staleEntries));
    }

    /// <summary>
    /// Every discovered module is placed, and nothing is placed that no longer exists.
    ///
    /// Without this, a new module would simply be absent from the map and the orientation test below
    /// would have nothing to say about it - the guard would silently stop covering the newest code,
    /// which is precisely when layering mistakes are made.
    /// </summary>
    [Fact]
    public void Every_Components_Module_Is_Placed_In_A_Layer()
    {
        var (nodes, _) = BuildClassGraph();
        var discovered = nodes.Select(ModuleOf).Where(m => m is not null).Cast<string>().Distinct().ToHashSet(StringComparer.Ordinal);

        var unplaced = discovered.Where(module => !ModuleLayers.ContainsKey(module)).Order(StringComparer.Ordinal).ToList();
        var vanished = ModuleLayers.Keys.Where(module => !discovered.Contains(module)).Order(StringComparer.Ordinal).ToList();

        Assert.True(unplaced.Count == 0,
            "Components modules with no layer. Place each one CONSCIOUSLY - the layer decides what it "
            + "may depend on, and defaulting it would let the next cycle in:\n  "
            + string.Join("\n  ", unplaced));
        Assert.True(vanished.Count == 0,
            "Layer map entries for modules that no longer exist - remove them so the map keeps "
            + "describing the real code:\n  " + string.Join("\n  ", vanished));
    }

    /// <summary>
    /// Dependencies run strictly downward. The inequality is STRICT, which also forbids
    /// same-layer edges: with no equal-layer edge and no upward edge, a cycle cannot exist at all -
    /// the guard stops describing the current state and starts making the bad state unreachable.
    ///
    /// A violation is not fixed by moving a module up. It is fixed by inverting the edge: raise a
    /// domain event so the upper module subscribes, read the shared entity locally, or move the code
    /// into the module that owns the concern. All three are used in the slices that emptied the
    /// baseline this map replaces.
    /// </summary>
    [Fact]
    public void Components_Module_Dependencies_Only_Point_Downward()
    {
        var (_, edges) = BuildClassGraph();

        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (from, targets) in edges)
        {
            var fromModule = ModuleOf(from);
            if (fromModule is null || !ModuleLayers.TryGetValue(fromModule, out var fromLayer)) continue;
            foreach (var to in targets)
            {
                var toModule = ModuleOf(to);
                if (toModule is null || toModule == fromModule) continue;
                if (!ModuleLayers.TryGetValue(toModule, out var toLayer)) continue;
                if (fromLayer <= toLayer)
                    violations.Add($"{fromModule}(L{fromLayer}) -> {toModule}(L{toLayer})  [{from.Name} -> {to.Name}]");
            }
        }

        Assert.True(violations.Count == 0,
            "Components module dependencies that do not run strictly downward. Invert the edge - a "
            + "domain event, an own-read over the shared entity, or moving the code to the module "
            + "that owns the concern - rather than raising the module's layer:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// The same downward rule, read from the source instead of the constructors. The constructor graph
    /// cannot see a static helper, a DTO, a domain event, a constant or a service located at run time
    /// (<c>GetRequiredService</c>): 49 such upward references had piled up behind a green guard
    /// (2026-09-25). Every <c>using Aetheus.Back.Components.X</c> and every fully qualified
    /// <c>Aetheus.Back.Components.X.Type</c> written in a module's files must point strictly downward.
    /// An unused upward <c>using</c> fails too: it is the first step of the next upward call.
    /// </summary>
    [Fact]
    public void Components_Module_Source_References_Only_Point_Downward()
    {
        var files = RepositoryScan.Enumerate(
            Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back", "Components"), "*.cs");
        Assert.True(files.Count >= 300,
            $"The Components source files scan is unexpectedly small ({files.Count} < 300); a guard that "
            + "scans nothing passes vacuously.");

        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            var module = System.Text.RegularExpressions.Regex.Match(
                text, @"(?m)^namespace\s+Aetheus\.Back\.Components\.(\w+)").Groups[1].Value;
            if (!ModuleLayers.TryGetValue(module, out var fromLayer)) continue;

            var lines = text.Split('\n');
            for (var number = 0; number < lines.Length; number++)
            {
                var line = lines[number];
                if (line.TrimStart().StartsWith("namespace ", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                    continue;
                foreach (System.Text.RegularExpressions.Match reference in System.Text.RegularExpressions.Regex.Matches(
                             line, @"Aetheus\.Back\.Components\.(\w+)"))
                {
                    var target = reference.Groups[1].Value;
                    if (target == module || !ModuleLayers.TryGetValue(target, out var toLayer)) continue;
                    if (fromLayer <= toLayer)
                        violations.Add($"{module}(L{fromLayer}) -> {target}(L{toLayer})  "
                            + $"{Path.GetRelativePath(RepositoryScan.Root, path)}:{number + 1}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Components source references that do not run strictly downward (using directives or "
            + "fully qualified names). Move the helper, event or constant to the module that owns it at "
            + "the lower layer, read the shared entity locally, or raise a domain event - never raise "
            + "the module's layer to make the reference legal:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// A <c>global using</c> reaches every file of the assembly, so it may only name a module that sits
    /// at or below every other one; anything higher would be an upward reference nobody wrote.
    /// </summary>
    [Fact]
    public void Global_Usings_Only_Name_Bottom_Modules()
    {
        var globals = RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back"), "*.cs")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(path), @"(?m)^\s*global\s+using\s+Aetheus\.Back\.Components\.(\w+)"))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(globals);

        // Layer 0 and below: storage and shared helpers, which depend on no other module.
        var tooHigh = globals.Where(module => !ModuleLayers.TryGetValue(module, out var layer) || layer > 0).ToList();
        Assert.True(tooHigh.Count == 0,
            "global using of a module above the bottom layers: " + string.Join(", ", tooHigh));
    }

    /// <summary>
    /// Returns every edge whose two endpoints belong to the same strongly-connected component
    /// (Tarjan), rendered as "From -&gt; To". Stable regardless of traversal order, unlike an
    /// enumeration of full cycle paths, so the frozen baseline does not churn when one cycle
    /// among several is fixed.
    /// </summary>
    private static HashSet<string> FindCyclicEdges(
        IReadOnlyCollection<string> nodes, Dictionary<string, HashSet<string>> edges)
    {
        var index = 0;
        var indices = new Dictionary<string, int>();
        var lowLinks = new Dictionary<string, int>();
        var onStack = new HashSet<string>();
        var stack = new Stack<string>();
        var componentOf = new Dictionary<string, int>();
        var componentSizes = new Dictionary<int, int>();
        var componentCount = 0;

        foreach (var node in nodes)
        {
            if (!indices.ContainsKey(node)) StrongConnect(node);
        }

        var cyclic = new HashSet<string>();
        foreach (var (from, targets) in edges)
        {
            foreach (var to in targets)
            {
                if (componentOf.TryGetValue(from, out var cf) && componentOf.TryGetValue(to, out var ct)
                    && cf == ct && (componentSizes[cf] > 1 || from == to))
                {
                    cyclic.Add($"{from} -> {to}");
                }
            }
        }
        return cyclic;

        void StrongConnect(string node)
        {
            indices[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);

            if (edges.TryGetValue(node, out var targets))
            {
                foreach (var next in targets)
                {
                    if (!indices.ContainsKey(next))
                    {
                        StrongConnect(next);
                        lowLinks[node] = Math.Min(lowLinks[node], lowLinks[next]);
                    }
                    else if (onStack.Contains(next))
                    {
                        lowLinks[node] = Math.Min(lowLinks[node], indices[next]);
                    }
                }
            }

            if (lowLinks[node] == indices[node])
            {
                var componentId = componentCount++;
                var size = 0;
                string member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    componentOf[member] = componentId;
                    size++;
                } while (!member.Equals(node, StringComparison.Ordinal));
                componentSizes[componentId] = size;
            }
        }
    }

    private static (List<Type> Nodes, Dictionary<Type, HashSet<Type>> Edges) BuildClassGraph()
    {
        var componentTypes = BackAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Namespace?.Contains(".Components.") == true
                        && !t.Name.Contains('<'))
            .ToList();

        var implementations = new Dictionary<Type, List<Type>>();
        foreach (var type in componentTypes)
        {
            foreach (var itf in type.GetInterfaces().Where(i => i.Assembly == BackAssembly))
            {
                if (!implementations.TryGetValue(itf, out var list))
                {
                    list = [];
                    implementations[itf] = list;
                }
                list.Add(type);
            }
        }

        var componentSet = componentTypes.ToHashSet();
        var edges = new Dictionary<Type, HashSet<Type>>();
        foreach (var type in componentTypes)
        {
            var targets = new HashSet<Type>();
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var param in ctor.GetParameters())
                {
                    foreach (var dep in ResolveDependencyTargets(Unwrap(param.ParameterType), implementations, componentSet))
                    {
                        if (dep != type) targets.Add(dep);
                    }
                }
            }
            edges[type] = targets;
        }

        return (componentTypes, edges);
    }

    private static Type Unwrap(Type type)
    {
        if (!type.IsGenericType) return type;
        var def = type.GetGenericTypeDefinition();
        if (def == typeof(Lazy<>) || def == typeof(Func<>) || def == typeof(IEnumerable<>))
        {
            return Unwrap(type.GetGenericArguments()[^1]);
        }
        return type;
    }

    private static IEnumerable<Type> ResolveDependencyTargets(
        Type parameterType, Dictionary<Type, List<Type>> implementations, HashSet<Type> componentSet)
    {
        if (parameterType.IsInterface)
        {
            return implementations.TryGetValue(parameterType, out var impls) ? impls : [];
        }
        return componentSet.Contains(parameterType) ? [parameterType] : [];
    }

    /// <summary>
    /// Returns every elementary cycle reachable in the graph, rendered as "A -> B -> A" strings.
    /// Uses iterative DFS per strongly-connected component; only the canonical rotation of each
    /// cycle is reported so a cycle appears once regardless of the DFS entry point.
    /// </summary>
    private static List<string> FindCycles<TNode>(
        IReadOnlyCollection<TNode> nodes, Dictionary<TNode, HashSet<TNode>> edges)
        where TNode : notnull
    {
        var cycles = new HashSet<string>();
        var visiting = new HashSet<TNode>();
        var visited = new HashSet<TNode>();
        var stack = new List<TNode>();

        foreach (var start in nodes)
        {
            Dfs(start);
        }

        return [.. cycles.Order()];

        void Dfs(TNode node)
        {
            if (visited.Contains(node) || visiting.Contains(node)) return;
            visiting.Add(node);
            stack.Add(node);
            if (edges.TryGetValue(node, out var targets))
            {
                foreach (var next in targets)
                {
                    if (visiting.Contains(next))
                    {
                        var cycleStart = stack.IndexOf(next);
                        var cycle = stack.Skip(cycleStart).ToList();
                        cycles.Add(Render(cycle));
                    }
                    else
                    {
                        Dfs(next);
                    }
                }
            }
            stack.RemoveAt(stack.Count - 1);
            visiting.Remove(node);
            visited.Add(node);
        }

        static string Render(List<TNode> cycle)
        {
            // Canonical rotation: start at the lexicographically smallest member so the same
            // cycle discovered from different entry points collapses to one report line.
            var names = cycle.Select(NameOf).ToList();
            var minIndex = names.IndexOf(names.Order().First());
            var rotated = names.Skip(minIndex).Concat(names.Take(minIndex)).ToList();
            return string.Join(" -> ", rotated) + " -> " + rotated[0];
        }

        static string NameOf(TNode node) => node is Type t ? t.Name : node.ToString() ?? "?";
    }

    private static string? ModuleOf(Type type)
    {
        const string marker = ".Components.";
        var ns = type.Namespace;
        var index = ns?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (ns is null || index < 0) return null;
        var rest = ns[(index + marker.Length)..];
        var dot = rest.IndexOf('.');
        return dot < 0 ? rest : rest[..dot];
    }
}
