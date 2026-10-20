// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Builds the display-oriented parent/leaf pipeline graph from parsed YAML definitions.</summary>
internal static class PipelineDependencyGraphBuilder
{
    internal static PipelineDependencyGroupsDto Build(
        IEnumerable<PipelineDto> pipelines,
        Func<string, PipelineYamlDefinition?> parse)
    {
        var source = pipelines.ToList();
        var items = BuildItems(source, source, parse);
        PopulateParents(items);
        return new PipelineDependencyGroupsDto
        {
            Parents = items.Where(item => item.References.Count > 0).ToList(),
            Leaves = items.Where(item => item.References.Count == 0).ToList()
        };
    }

    internal static List<PipelineDependencyDto> BuildItems(
        IEnumerable<PipelineDto> pipelines,
        IEnumerable<PipelineDto> identities,
        Func<string, PipelineYamlDefinition?> parse)
    {
        var pipelineLookup = identities
            .GroupBy(p => (p.ProjectId, p.Name), new PipelineIdentityComparer())
            .ToDictionary(group => group.Key, group => group.First().Id, new PipelineIdentityComparer());
        var items = new List<PipelineDependencyDto>();
        foreach (var pipeline in pipelines)
        {
            var definition = parse(pipeline.YamlDefinition);
            var referenceNames = definition is not null ? ExtractReferences(definition) : [];
            var references = referenceNames.Select(name => new PipelineDependencyReferenceDto(
                pipelineLookup.GetValueOrDefault((pipeline.ProjectId, name)), name)).ToList();
            var item = new PipelineDependencyDto
            {
                Id = pipeline.Id,
                Name = pipeline.Name,
                ProjectId = pipeline.ProjectId,
                ProjectName = pipeline.ProjectName,
                TriggerType = ParseTriggerType(definition?.Trigger, pipeline.TriggerType),
                RecentRuns = pipeline.RecentRuns,
                References = references,
                OwnerUsername = pipeline.OwnerUsername,
                OwnerInactive = pipeline.OwnerInactive
            };
            items.Add(item);
        }
        return items;
    }

    private static void PopulateParents(List<PipelineDependencyDto> items)
    {
        var lookup = items.ToDictionary(item => item.Id);
        foreach (var parent in items)
        {
            foreach (var childReference in parent.References)
            {
                if (childReference.Id is not { } childId || !lookup.TryGetValue(childId, out var child))
                    continue;

                child.Parents.Add(new PipelineDependencyReferenceDto(parent.Id, parent.Name));
            }
        }

        foreach (var item in items)
            item.Parents.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
    }

    private static List<string> ExtractReferences(PipelineYamlDefinition definition) =>
        definition.OnSuccess.Select(trigger => trigger.Pipeline)
            .Concat(definition.Stages.SelectMany(stage => stage.Steps
                .Concat(stage.Jobs.SelectMany(job => job.Steps))
                .Where(step => string.Equals(step.Type, "trigger", StringComparison.OrdinalIgnoreCase))
                .Select(step => step.Pipeline)))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static PipelineTriggerType ParseTriggerType(string? trigger, PipelineTriggerType fallback) =>
        trigger?.ToLowerInvariant() switch
        {
            "webhook" => PipelineTriggerType.Webhook,
            "schedule" => PipelineTriggerType.Schedule,
            "manual" => PipelineTriggerType.Manual,
            _ => fallback
        };

    private sealed class PipelineIdentityComparer : IEqualityComparer<(int? ProjectId, string Name)>
    {
        public bool Equals((int? ProjectId, string Name) x, (int? ProjectId, string Name) y) =>
            x.ProjectId == y.ProjectId && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((int? ProjectId, string Name) value) =>
            HashCode.Combine(value.ProjectId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }
}
