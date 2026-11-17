// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Pipelines;

internal sealed class PipelineTemplateEditorCoordinator(ApiClient api)
{
    public Task<List<PipelineTemplateSummaryDto>> LoadTemplatesAsync() =>
        api.PipelineTemplates.GetPipelineTemplatesAsync();

    public async Task<string?> LoadBaseYamlAsync(
        string yaml, IReadOnlyCollection<PipelineTemplateSummaryDto> templates)
    {
        var reference = PipelineTemplateReferenceHelper.Parse(yaml);
        if (reference is null) return null;
        var summary = FindSummary(reference.Name, templates);
        if (summary is null) return null;
        var template = await api.PipelineTemplates.GetPipelineTemplateAsync(summary.Id);
        if (template is null) return null;
        var version = reference.Version ?? template.Version;
        var templateYaml = version == template.Version
            ? template.YamlContent
            : (await api.PipelineTemplates.GetPipelineTemplateVersionAsync(summary.Id, version))?.YamlContent;
        var values = ParameterValues(templateYaml, yaml);
        return values is null
            ? null
            : await api.PipelineTemplates.ResolvePipelineTemplateAsync(summary.Id, version, values);
    }

    /// <summary>Pipeline parameter defaults override the template's defaults during resolution. A
    /// missing required value suppresses the request because the template cannot yet be
    /// resolved.</summary>
    internal static Dictionary<string, string>? ParameterValues(string? templateYaml, string? pipelineYaml)
    {
        var serializer = new YamlSerializationService();
        var declared = templateYaml is null ? [] : serializer.Parse(templateYaml)?.Parameters ?? [];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in declared.Where(parameter => parameter.Default is not null))
            values[parameter.Name] = parameter.Default!;
        var overrides = pipelineYaml is null ? [] : serializer.Parse(pipelineYaml)?.Parameters ?? [];
        // R1-84: the back refuses a parameter the template does not declare (HTTP 400), so a stale key
        // left in the pipeline YAML after a template change is dropped here, never sent.
        var declaredNames = declared.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var parameter in overrides.Where(parameter => parameter.Default is not null && declaredNames.Contains(parameter.Name)))
            values[parameter.Name] = parameter.Default!;
        // R-44: the back refuses (HTTP 400) a required parameter whose value is empty, and a
        // `${{ parameters.x }}` reference that no declaration of the template covers, so neither request is sent.
        if (declared.Any(parameter => parameter.Required && string.IsNullOrWhiteSpace(values.GetValueOrDefault(parameter.Name))))
            return null;
        return templateYaml is not null && ReferencesUndeclaredParameter(templateYaml, declaredNames) ? null : values;
    }

    private static readonly Regex ParameterReference = new(
        @"\$\{\{\s*parameters\.([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    private static bool ReferencesUndeclaredParameter(string templateYaml, IEnumerable<string> declaredNames)
    {
        var declaredIgnoringCase = declaredNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ParameterReference.Matches(templateYaml)
            .Any(match => !declaredIgnoringCase.Contains(match.Groups[1].Value));
    }

    public async Task<PipelineTemplateSelection?> SelectAsync(int templateId, string pipelineName)
    {
        var template = await api.PipelineTemplates.GetPipelineTemplateAsync(templateId);
        if (template is null) return null;
        var serializer = new YamlSerializationService();
        var templateDefinition = serializer.Parse(template.YamlContent);
        if (templateDefinition is null) return null;
        // A required parameter without a default has no value before the user fills it in: the base is
        // then the template as written, its parameter references still in place.
        var values = ParameterValues(template.YamlContent, null);
        var resolvedBaseYaml = values is null
            ? template.YamlContent
            : await api.PipelineTemplates.ResolvePipelineTemplateAsync(template.Id, template.Version, values);
        if (resolvedBaseYaml is null) return null;
        var yaml = serializer.Serialize(new PipelineYamlDefinition
        {
            Name = string.IsNullOrWhiteSpace(pipelineName) ? template.Name : pipelineName,
            Extends = $"{template.Name}@{template.Version}",
            Stages = []
        });
        return new PipelineTemplateSelection(yaml, resolvedBaseYaml, templateDefinition.Parameters);
    }

    public static string ApplyParameters(
        PipelineTemplateSelection selection,
        IReadOnlyDictionary<string, string> values)
    {
        var serializer = new YamlSerializationService();
        var definition = serializer.Parse(selection.Yaml)
            ?? throw new InvalidOperationException("The generated pipeline YAML could not be parsed.");
        var parameters = selection.Parameters
            .Select(parameter => parameter with
            {
                Default = values.TryGetValue(parameter.Name, out var value) ? value : parameter.Default
            })
            .ToList();
        return serializer.Serialize(definition with { Parameters = parameters });
    }

    public string? Pin(string yaml, IReadOnlyCollection<PipelineTemplateSummaryDto> templates)
    {
        var reference = PipelineTemplateReferenceHelper.Parse(yaml);
        if (reference is not { Version: null }) return null;
        var summary = FindSummary(reference.Name, templates);
        return summary is null
            ? null
            : PipelineTemplateReferenceHelper.Pin(yaml, summary.Name, summary.Version);
    }

    public async Task<PipelineFleetItemDto?> FindFleetItemAsync(int pipelineId)
    {
        try { return await api.Pipelines.GetPipelineFleetItemAsync(pipelineId); }
        catch (HttpRequestException) { return null; }
    }

    private static PipelineTemplateSummaryDto? FindSummary(
        string name, IEnumerable<PipelineTemplateSummaryDto> templates) =>
        templates.FirstOrDefault(template =>
            string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase));
}
