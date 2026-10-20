// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

public sealed class PipelineTemplateResolver(IPipelineTemplateRepository repo) : IPipelineTemplateResolver
{
    private const int MaxInheritanceDepth = 32;
    private const int MaxResolvedYamlLength = 1_000_000;
    private static readonly Regex ParameterPattern = new(
        @"\$\{\{\s*parameters\.([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    public async Task<PipelineTemplateResolution> ResolveAsync(
        string yamlContent,
        int organizationId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        var definition = Parse(yamlContent, "pipeline");
        var rootReference = ParseReference(definition.Extends);
        var composed = await ComposeAsync(definition, organizationId, [], ct, rootReference, 0).ConfigureAwait(false);
        var serialized = definition.Extends is null ? yamlContent : YamlParsingHelper.Serializer.Serialize(composed);
        var resolvedYaml = parameters is null
            ? serialized
            : ResolveParameters(serialized, composed.Parameters, parameters);
        if (resolvedYaml.Length > MaxResolvedYamlLength)
            throw new BadRequestException(
                $"Resolved pipeline YAML exceeds the {MaxResolvedYamlLength} character safety limit.");
        var resolvedDefinition = parameters is null ? composed : Parse(resolvedYaml, "resolved pipeline");
        return new PipelineTemplateResolution(
            resolvedDefinition,
            resolvedYaml,
            rootReference?.Name,
            rootReference?.Version,
            rootReference?.ResolvedVersion,
            rootReference?.IsLegacy == true);
    }

    private async Task<PipelineYamlDefinition> ComposeAsync(
        PipelineYamlDefinition child,
        int organizationId,
        HashSet<string> visited,
        CancellationToken ct,
        TemplateReference? parsedReference = null,
        int depth = 0)
    {
        if (depth > MaxInheritanceDepth)
            throw new BadRequestException(
                $"Pipeline template inheritance exceeds the maximum depth of {MaxInheritanceDepth}.");
        var reference = parsedReference ?? ParseReference(child.Extends);
        if (reference is null) return child;

        // An organization's own template wins; otherwise the shipped delivery template the platform
        // shares with every organization, so a new organization's projects can use the setup wizard
        // without a copy of the catalog.
        var template = await repo.FindTemplateByNameAsync(reference.Name, organizationId, ct).ConfigureAwait(false)
            ?? await repo.FindSharedTemplateByNameAsync(reference.Name, ct).ConfigureAwait(false)
            ?? throw new BadRequestException(
                $"Pipeline template '{reference.Name}' was not found in organization {organizationId}.");
        var versionNumber = reference.Version ?? template.LatestVersion;
        reference.ResolvedVersion = versionNumber;
        var cycleKey = $"{template.Id}@{versionNumber}";
        if (!visited.Add(cycleKey))
            throw new BadRequestException($"Cyclic template inheritance detected at '{reference.Name}@{versionNumber}'.");

        var version = template.Versions.FirstOrDefault(item => item.Version == versionNumber)
            ?? await repo.GetTemplateVersionAsync(template.Id, versionNumber, ct).ConfigureAwait(false)
            ?? throw new BadRequestException(
                $"Pipeline template version '{reference.Name}@{versionNumber}' does not exist.");
        var baseDefinition = Parse(version.YamlContent, $"template '{reference.Name}@{versionNumber}'");
        var composedBase = await ComposeAsync(
            baseDefinition, organizationId, visited, ct, depth: depth + 1).ConfigureAwait(false);
        visited.Remove(cycleKey);
        return Merge(composedBase, child);
    }

    private static PipelineYamlDefinition Merge(PipelineYamlDefinition parent, PipelineYamlDefinition child)
    {
        var variables = new Dictionary<string, string>(parent.Variables, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in child.Variables) variables[name] = value;

        var parameters = parent.Parameters.ToList();
        foreach (var item in child.Parameters)
            ReplaceOrAppend(parameters, item, parameter => parameter.Name);

        return child with
        {
            Name = child.Name.Length > 0 ? child.Name : parent.Name,
            Trigger = child.Trigger != "manual" ? child.Trigger : parent.Trigger,
            ProjectType = child.ProjectType ?? parent.ProjectType,
            Schedule = child.Schedule ?? parent.Schedule,
            SupersedeRunning = child.SupersedeRunning ?? parent.SupersedeRunning,
            ReleaseNumber = child.ReleaseNumber ?? parent.ReleaseNumber,
            Branches = child.Branches.Count > 0 ? child.Branches : parent.Branches,
            Extends = null,
            Variables = variables,
            VariableLibraries = child.VariableLibraries.Count > 0 ? child.VariableLibraries : parent.VariableLibraries,
            Vaults = child.Vaults.Count > 0 ? child.Vaults : parent.Vaults,
            Parameters = parameters,
            Stages = MergeStages(parent.Stages, child.Stages),
            Isolation = child.Isolation ?? parent.Isolation,
            OnSuccess = child.OnSuccess.Count > 0 ? child.OnSuccess : parent.OnSuccess,
            Requires = MergeRequires(parent.Requires, child.Requires)
        };
    }

    /// <summary>Template requirements are merged with the extending pipeline's requirements so
    /// launch preflight verifies both.</summary>
    private static PipelineRequiresDefinition? MergeRequires(
        PipelineRequiresDefinition? parent,
        PipelineRequiresDefinition? child)
    {
        if (parent is null) return child;
        if (child is null) return parent;
        return new PipelineRequiresDefinition
        {
            Libraries = Union(parent.Libraries, child.Libraries),
            Vaults = Union(parent.Vaults, child.Vaults),
            Environments = Union(parent.Environments, child.Environments),
            Capabilities = Union(parent.Capabilities, child.Capabilities)
        };

        static List<string> Union(List<string> first, List<string> second) =>
            first.Concat(second).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<PipelineStageDefinition> MergeStages(
        IReadOnlyList<PipelineStageDefinition> parent,
        IReadOnlyList<PipelineStageDefinition> child)
    {
        var result = parent.ToList();
        foreach (var item in child)
        {
            var index = CaseInsensitiveNameLookup.FindIndex(result, item.Name, stage => stage.Name);
            if (item.Remove)
            {
                if (index >= 0) result.RemoveAt(index);
                continue;
            }

            var merged = index < 0 ? item : item with
            {
                Jobs = MergeJobs(result[index].Jobs, item.Jobs),
                Steps = MergeSteps(result[index].Steps, item.Steps)
            };
            if (index >= 0) result[index] = merged; else result.Add(merged);
        }
        return result;
    }

    private static List<PipelineJobDefinition> MergeJobs(
        IReadOnlyList<PipelineJobDefinition> parent,
        IReadOnlyList<PipelineJobDefinition> child)
    {
        var result = parent.ToList();
        foreach (var item in child)
        {
            var index = CaseInsensitiveNameLookup.FindIndex(result, item.Name, job => job.Name);
            if (item.Remove)
            {
                if (index >= 0) result.RemoveAt(index);
                continue;
            }
            var merged = index < 0 ? item : item with { Steps = MergeSteps(result[index].Steps, item.Steps) };
            if (index >= 0) result[index] = merged; else result.Add(merged);
        }
        return result;
    }

    private static List<PipelineStepDefinition> MergeSteps(
        IReadOnlyList<PipelineStepDefinition> parent,
        IReadOnlyList<PipelineStepDefinition> child)
    {
        var result = parent.ToList();
        foreach (var item in child)
        {
            var index = CaseInsensitiveNameLookup.FindIndex(result, item.Name, step => step.Name);
            if (item.Remove)
            {
                if (index >= 0) result.RemoveAt(index);
            }
            else if (index >= 0)
            {
                result[index] = item;
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
    }

    private static string ResolveParameters(
        string yaml,
        IReadOnlyList<PipelineTemplateParameterDefinition> declarations,
        IReadOnlyDictionary<string, string>? supplied)
    {
        if (!PipelineParameterResolver.TryResolve(
                declarations, supplied, out var values, out var parameterErrors))
            throw new BadRequestException(string.Join(" ", parameterErrors));

        var declaredNames = declarations
            .Select(parameter => parameter.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolved = ParameterPattern.Replace(yaml, match =>
        {
            var name = match.Groups[1].Value;
            if (values.TryGetValue(name, out var value))
                return value;
            return declaredNames.Contains(name) ? string.Empty : match.Value;
        });
        var unresolved = ParameterPattern.Match(resolved);
        if (unresolved.Success)
            throw new BadRequestException(
                $"Pipeline template parameter '{unresolved.Groups[1].Value}' could not be resolved.");
        return resolved;
    }

    private static PipelineYamlDefinition Parse(string yaml, string source)
    {
        try
        {
            return YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml)
                ?? throw new BadRequestException($"The {source} YAML is empty.");
        }
        catch (Exception exception) when (exception is YamlDotNet.Core.YamlException
            or InvalidOperationException or ArgumentException)
        {
            throw new BadRequestException($"The {source} YAML is invalid: {exception.Message}");
        }
    }

    private static TemplateReference? ParseReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var separator = value.LastIndexOf('@');
        if (separator < 1) return new TemplateReference(value.Trim(), null);
        var name = value[..separator].Trim();
        if (!int.TryParse(value[(separator + 1)..], out var version) || version < 1)
            throw new BadRequestException($"Invalid pinned pipeline template reference '{value}'. Expected name@N.");
        return new TemplateReference(name, version);
    }

    private static void ReplaceOrAppend<T>(List<T> items, T item, Func<T, string> selector)
    {
        var index = CaseInsensitiveNameLookup.FindIndex(items, selector(item), selector);
        if (index >= 0) items[index] = item; else items.Add(item);
    }

    private sealed class TemplateReference(string name, int? version)
    {
        public string Name { get; } = name;
        public int? Version { get; } = version;
        public int? ResolvedVersion { get; set; }
        public bool IsLegacy => Version is null;
    }
}
