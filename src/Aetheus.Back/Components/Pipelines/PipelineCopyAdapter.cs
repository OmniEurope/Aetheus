// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Rewrites whole-token references to copied pipelines, libraries, vaults and
/// APPLICATION_PREFIX while preserving YAML formatting. Other content remains unchanged, with
/// source-project references, pinned execution targets, repository sources, addresses and uncopied
/// pipeline references reported for review.</summary>
internal static partial class PipelineCopyAdapter
{
    private const int MaxDetails = 5;

    [GeneratedRegex(@"https?://(?<host>[A-Za-z0-9.-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern { get; }

    [GeneratedRegex(@"(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern { get; }

    private static readonly HashSet<string> LocalHosts =
        new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "0.0.0.0" };

    /// <summary>The copy's name: the source prefix replaced by the new one, or the new prefix put in
    /// front of a name that does not start with the source prefix.</summary>
    internal static string NewName(string sourceName, string sourcePrefix, string prefix) =>
        !string.IsNullOrEmpty(sourcePrefix)
            && sourceName.StartsWith(sourcePrefix + "-", StringComparison.Ordinal)
            && sourceName.Length > sourcePrefix.Length + 1
            ? prefix + sourceName[sourcePrefix.Length..]
            : $"{prefix}-{sourceName}";

    /// <summary>A library or vault keeps its name unless it carries the source prefix: a generic name
    /// (<c>application-prod-host</c>) is what a template asks for and must not change.</summary>
    internal static string NewResourceName(string sourceName, string sourcePrefix, string prefix) =>
        !string.IsNullOrEmpty(sourcePrefix)
            && sourceName.StartsWith(sourcePrefix + "-", StringComparison.OrdinalIgnoreCase)
            && sourceName.Length > sourcePrefix.Length + 1
            ? prefix + sourceName[sourcePrefix.Length..]
            : sourceName;

    /// <param name="yaml">The source definition.</param>
    /// <param name="renames">Source name to copy name: the copied pipelines, libraries and vaults.</param>
    /// <param name="sourcePrefix">The source project's prefix.</param>
    /// <param name="prefix">The copies' prefix.</param>
    /// <param name="copiedPipelines">The names the copies will have; a reference outside it is reported.</param>
    internal static (string Yaml, List<PipelineCopyIssueDto> Issues) Adapt(
        string yaml,
        IReadOnlyDictionary<string, string> renames,
        string sourcePrefix,
        string prefix,
        IReadOnlySet<string> copiedPipelines)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(renames);
        var adapted = yaml;
        // Longest first, so a name never rewrites the beginning of a longer one before it is matched.
        foreach (var (from, to) in renames.OrderByDescending(pair => pair.Key.Length))
            if (!string.Equals(from, to, StringComparison.Ordinal))
                adapted = Token(from).Replace(adapted, to.Replace("$", "$$", StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(sourcePrefix))
            adapted = new Regex(
                    @"(?<lead>APPLICATION_PREFIX\s*:\s*[""']?)" + Regex.Escape(sourcePrefix) + @"(?=[""']?\s*$)",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant)
                .Replace(adapted, "${lead}" + prefix.Replace("$", "$$", StringComparison.Ordinal));

        var issues = new List<PipelineCopyIssueDto>();
        AddIssue(issues, PipelineCopyIssueKind.SourceProjectNameRemains,
            RemainingMentions(adapted, sourcePrefix, renames.Values.Append(prefix)));
        if (YamlParsingHelper.ParseAndValidate(adapted) is { } definition)
            AddStructuralIssues(definition, copiedPipelines, issues);
        AddIssue(issues, PipelineCopyIssueKind.HardCodedHost, Hosts(adapted));
        return (adapted, issues);
    }

    private static Regex Token(string name) => new(
        @"(?<![A-Za-z0-9_-])" + Regex.Escape(name) + @"(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant);

    /// <summary>The words still containing the source prefix once the known names are rewritten. The
    /// copies' own names are blanked first, so a new prefix that contains the old one is not reported.</summary>
    private static IEnumerable<string> RemainingMentions(string adapted, string sourcePrefix, IEnumerable<string> newNames)
    {
        if (string.IsNullOrEmpty(sourcePrefix)) return [];
        var scanned = adapted;
        foreach (var name in newNames.Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.Ordinal)
                     .OrderByDescending(name => name.Length))
            scanned = Token(name).Replace(scanned, " ");
        var pattern = new Regex(
            @"[A-Za-z0-9_./:@-]*(?<![A-Za-z0-9])" + Regex.Escape(sourcePrefix) + @"(?![A-Za-z0-9])[A-Za-z0-9_./:@-]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return pattern.Matches(scanned).Select(match => match.Value);
    }

    private static IEnumerable<string> Hosts(string adapted) =>
        UrlPattern.Matches(adapted).Select(match => match.Groups["host"].Value)
            .Concat(Ipv4Pattern.Matches(adapted).Select(match => match.Value))
            .Where(host => host.Length > 0 && !LocalHosts.Contains(host.TrimEnd('.')));

    private static void AddStructuralIssues(
        PipelineYamlDefinition definition, IReadOnlySet<string> copiedPipelines, List<PipelineCopyIssueDto> issues)
    {
        var jobs = definition.Stages.SelectMany(stage => stage.Jobs).ToList();
        AddIssue(issues, PipelineCopyIssueKind.PinnedAgent,
            definition.Stages.Select(stage => stage.Agent).Concat(jobs.Select(job => job.Agent)).Where(IsLiteral));

        var steps = definition.Stages.SelectMany(stage => stage.Steps).Concat(jobs.SelectMany(job => job.Steps)).ToList();
        AddIssue(issues, PipelineCopyIssueKind.VirtualHostName, steps.Select(step => step.ServerName).Where(IsLiteral)!);
        AddIssue(issues, PipelineCopyIssueKind.SourceRepository,
            new[] { definition.Source?.Repository }.Where(IsLiteral)!);
        AddIssue(issues, PipelineCopyIssueKind.UncopiedPipeline,
            definition.OnSuccess.Select(trigger => trigger.Pipeline)
                .Concat(steps.Select(step => step.Pipeline))
                .Concat(steps.Select(step => step.ArtifactSourcePipeline))
                .Where(IsLiteral)
                .Where(name => !copiedPipelines.Contains(name!))!);
    }

    /// <summary>A written value, not a variable reference resolved at launch.</summary>
    private static bool IsLiteral(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains("$(", StringComparison.Ordinal)
        && !value.Contains("${{", StringComparison.Ordinal);

    private static void AddIssue(List<PipelineCopyIssueDto> issues, PipelineCopyIssueKind kind, IEnumerable<string> found)
    {
        var distinct = found.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0) return;
        var detail = string.Join(", ", distinct.Take(MaxDetails));
        if (distinct.Count > MaxDetails) detail += $" (+{distinct.Count - MaxDetails})";
        issues.Add(new PipelineCopyIssueDto { Kind = kind, Detail = detail });
    }
}
