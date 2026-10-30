// SPDX-License-Identifier: EUPL-1.2
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aetheus.Back.Services;

public static class YamlParsingHelper
{
    /// <summary>Skips unknown YAML keys so delivery definitions can reach a backend that does not
    /// yet implement them, while reporting those keys through
    /// PipelineYamlDiagnostics.AppendUnknownPropertyWarnings. Invalid shapes, conversions and YAML
    /// syntax still throw.</summary>
    public static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Server configuration YAML keeps the strict contract: it is written by hand for one server and
    /// applied as a whole, so a misspelled key must be refused rather than skipped.
    /// </summary>
    public static readonly IDeserializer ServerConfigDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithAttributeOverride<PipelineIsolationDefinition>(
            isolation => isolation.IsContainer,
            new YamlIgnoreAttribute())
        // A computed property: written out, it would come back as an unknown `is_empty` key reported
        // as a warning on every resolved pipeline carrying a template's requires:.
        .WithAttributeOverride<PipelineRequiresDefinition>(
            requires => requires.IsEmpty,
            new YamlIgnoreAttribute())
        .Build();

    /// <summary>
    /// Parses a pipeline YAML document and applies the lightweight semantic checks shared by
    /// PipelineService and PipelineRunService. Returns <c>null</c> when the YAML is invalid.
    /// </summary>
    public static PipelineYamlDefinition? ParseAndValidate(string yaml, ILogger? logger = null)
        => ParseAndValidate(yaml, out _, logger);

    /// <summary>
    /// Same as <see cref="ParseAndValidate(string, ILogger?)"/>, and says why the YAML is refused:
    /// <paramref name="error"/> is the parser's own message (it names the line and column of a syntax
    /// error) or the semantic check that failed, and <c>null</c> when the YAML is valid.
    /// </summary>
    public static PipelineYamlDefinition? ParseAndValidate(string yaml, out string? error, ILogger? logger = null)
    {
        error = null;
        try
        {
            var definition = Deserializer.Deserialize<PipelineYamlDefinition>(yaml);
            if (definition is null)
            {
                error = "the document defines nothing";
                return null;
            }

            if (definition.VariableLibraries.Any(string.IsNullOrWhiteSpace))
            {
                error = "variable_libraries contains an empty entry";
                return null;
            }
            if (definition.Vaults.Any(string.IsNullOrWhiteSpace))
            {
                error = "vaults contains an empty entry";
                return null;
            }

            return definition;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            error = DescribeParseError(ex);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            logger?.LogWarning(ex, "Unexpected error parsing pipeline YAML");
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The parser's message with the place it refers to. A YamlDotNet syntax error keeps its line and
    /// column on the exception, not in its message ("While scanning a plain scalar value, found invalid
    /// mapping." alone does not say where), so a reader of a refusal can find the offending line.
    /// </summary>
    public static string DescribeParseError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is YamlDotNet.Core.YamlException { Start.Line: > 0 } yaml
            ? $"line {yaml.Start.Line}, column {yaml.Start.Column}: {yaml.Message}"
            : exception.Message;
    }

    /// <summary>
    /// Flattens stages that use the <c>jobs</c> property into a flat list of effective stages.
    /// Each job becomes its own pseudo-stage with <c>GroupName = parent stage name</c>.
    /// Jobs within a stage run in parallel by default: they all depend on the prerequisite stages'
    /// jobs. A job may also declare <c>depends_on</c> on sibling job names to form an intra-stage
    /// DAG (S-TECH-55) - those sibling names map directly to pseudo-stage names. Cross-stage
    /// <c>depends_on</c> resolves to ALL jobs of the referenced stage so the next stage waits for
    /// every parallel job to complete.
    /// Stages using the legacy <c>steps</c> format keep their name and steps; only a <c>depends_on</c>
    /// naming a jobs-stage is expanded to that stage's jobs, as for a job.
    /// </summary>
    public static List<PipelineStageDefinition> FlattenJobs(PipelineYamlDefinition definition)
    {
        // Build a map: stage name → all job names (for resolving cross-stage depends_on)
        var allJobsPerStage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in definition.Stages)
        {
            allJobsPerStage[s.Name] = s.Jobs.Count > 0
                ? s.Jobs.Select(j => j.Name).ToList()
                : [s.Name];
        }

        var result = new List<PipelineStageDefinition>();
        foreach (var stage in definition.Stages)
            AddFlattenedStage(definition.Isolation, stage, allJobsPerStage, result);
        return result;
    }

    private static void AddFlattenedStage(
        PipelineIsolationDefinition? defaultIsolation,
        PipelineStageDefinition stage,
        IReadOnlyDictionary<string, List<string>> allJobsPerStage,
        ICollection<PipelineStageDefinition> result)
    {
        var isLegacy = stage.Jobs.Count == 0;
        var externalDependencies = ExpandDependencies(stage.DependsOn, allJobsPerStage, keepWrittenNames: isLegacy);
        if (isLegacy)
        {
            // A legacy (steps) stage keeps its own name and steps; only its dependencies are rewritten.
            var legacy = externalDependencies.SequenceEqual(stage.DependsOn)
                ? stage
                : stage with { DependsOn = externalDependencies };
            result.Add(legacy.Isolation is null && defaultIsolation is not null
                ? legacy with { Isolation = defaultIsolation }
                : legacy);
            return;
        }
        foreach (var job in stage.Jobs)
            result.Add(CreateJobStage(defaultIsolation, stage, job, externalDependencies));
    }

    /// <summary>
    /// A dependency on a jobs-stage stands for all its jobs, the only names that carry step runs. With
    /// <paramref name="keepWrittenNames"/> (a legacy stage), a dependency on a stage that is not a
    /// jobs-stage stays exactly as written, so a wrong case is still reported by the definition validator
    /// instead of being corrected silently.
    /// </summary>
    private static List<string> ExpandDependencies(
        IEnumerable<string> dependsOn,
        IReadOnlyDictionary<string, List<string>> allJobsPerStage,
        bool keepWrittenNames) =>
        [.. dependsOn.SelectMany(dependency =>
        {
            var expanded = allJobsPerStage.GetValueOrDefault(dependency, [dependency]);
            return keepWrittenNames && expanded is [var only] && string.Equals(only, dependency, StringComparison.OrdinalIgnoreCase)
                ? [dependency]
                : expanded;
        })];

    private static PipelineStageDefinition CreateJobStage(
        PipelineIsolationDefinition? defaultIsolation,
        PipelineStageDefinition stage,
        PipelineJobDefinition job,
        IReadOnlyCollection<string> externalDependencies)
    {
        var variables = new Dictionary<string, string>(stage.Variables, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in job.Variables) variables[key] = value;
        var dependencies = job.DependsOn.Count > 0
            ? externalDependencies.Concat(job.DependsOn).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : externalDependencies.ToList();
        return new PipelineStageDefinition
        {
            Name = job.Name,
            Agent = !string.IsNullOrEmpty(job.Agent) ? job.Agent : stage.Agent,
            Os = !string.IsNullOrEmpty(job.Os) ? job.Os : stage.Os,
            Group = stage.Name,
            ExecutionRole = job.ExecutionRole ?? stage.ExecutionRole,
            Environment = job.Environment ?? stage.Environment,
            ApprovalTimeoutMinutes = stage.ApprovalTimeoutMinutes,
            ResumeWindowHours = stage.ResumeWindowHours,
            DeploymentRole = stage.DeploymentRole,
            Pool = job.Pool ?? stage.Pool,
            Condition = job.Condition ?? stage.Condition,
            DependsOn = dependencies,
            Variables = variables,
            Steps = job.Steps,
            Matrix = job.Matrix ?? stage.Matrix,
            Strategy = job.Strategy ?? stage.Strategy,
            Artifacts = job.Artifacts.Count > 0 ? job.Artifacts : stage.Artifacts,
            Isolation = job.Isolation ?? stage.Isolation ?? defaultIsolation
        };
    }

    public static List<string> ParseVaultNames(string yamlDefinition)
    {
        if (string.IsNullOrWhiteSpace(yamlDefinition))
            return [];

        try
        {
            var def = Deserializer.Deserialize<PipelineYamlDefinition>(yamlDefinition);
            return def?.Vaults?
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }
}
