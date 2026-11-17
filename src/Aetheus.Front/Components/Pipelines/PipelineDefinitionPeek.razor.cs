// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Read-only viewer for a referenced pipeline's live definition, shown as the right split pane on the run
/// YAML tab and the pipeline edit page. Resolves the pipeline by name within the project, renders its YAML
/// with its own clickable <c>pipeline:</c> refs (navigating within this pane), and offers wrap/copy/close.
/// </summary>
public partial class PipelineDefinitionPeek
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    /// <summary>The pipeline to display, set by the parent (the ref the user clicked). Null hides the pane.</summary>
    [Parameter, EditorRequired] public string? PipelineName { get; set; }
    [Parameter] public int? ProjectId { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>R-35: a template reference (<c>name@version</c>, or the legacy bare <c>name</c> = latest) to
    /// display instead of a pipeline, set by the parent when the user Ctrl-clicks an <c>extends:</c> line. Its
    /// own <c>extends:</c> lines are followable in this pane, so inherited templates are walked in turn.</summary>
    [Parameter] public string? TemplateReference { get; set; }

    private PipelineDto? _current;
    private string? _currentName;
    private string? _lastExternalName;
    private string? _lastExternalTemplate;
    private PipelineTemplateSummaryDto? _templateSummary;
    private PipelineTemplateVersionDto? _template;
    private bool _loading;
    private bool _wrap;
    private int _requestGeneration;

    private bool _targetIsTemplate;
    private bool ShowsTemplate => _targetIsTemplate;
    private string? CurrentYaml => ShowsTemplate ? _template?.YamlContent : _current?.YamlDefinition;
    private bool HasContent => ShowsTemplate ? _template is not null : _current is not null;

    protected override async Task OnParametersSetAsync()
    {
        // Only react to the parent CHANGING the requested pipeline or template - internal ref navigation must
        // not be overridden by a re-render carrying the same parameters.
        if (PipelineName == _lastExternalName && TemplateReference == _lastExternalTemplate) return;
        var templateChanged = TemplateReference != _lastExternalTemplate;
        _lastExternalName = PipelineName;
        _lastExternalTemplate = TemplateReference;
        _wrap = false;
        if (templateChanged && TemplateReference is not null)
        {
            await PeekTemplateAsync(TemplateReference);
            return;
        }
        if (PipelineName is null)
        {
            Interlocked.Increment(ref _requestGeneration);
            ClearTarget();
            _loading = false;
            return;
        }
        await PeekAsync(PipelineName);
    }

    private void ClearTarget()
    {
        _current = null;
        _currentName = null;
        _templateSummary = null;
        _template = null;
        _targetIsTemplate = false;
    }

    private async Task PeekTemplateAsync(string reference)
    {
        var generation = Interlocked.Increment(ref _requestGeneration);
        ClearTarget();
        _targetIsTemplate = true;
        _currentName = reference;
        _loading = true;
        StateHasChanged();
        try
        {
            var parsed = PipelineTemplateReferenceHelper.ParseReference(reference);
            if (parsed is null) return;
            // The catalog the caller may read; a template of the caller's own organization wins over a shared one
            // of the same name (the resolver's rule).
            var summaries = await Api.PipelineTemplates.GetPipelineTemplatesAsync();
            if (generation != _requestGeneration) return;
            var summary = summaries
                .Where(template => string.Equals(template.Name, parsed.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(template => template.IsShared)
                .FirstOrDefault();
            if (summary is null) return;
            var version = parsed.Version ?? summary.Version;
            var loaded = version == summary.Version
                ? (await Api.PipelineTemplates.GetPipelineTemplateAsync(summary.Id)) is { } latest
                    ? new PipelineTemplateVersionDto { Version = latest.Version, YamlContent = latest.YamlContent }
                    : null
                : await Api.PipelineTemplates.GetPipelineTemplateVersionAsync(summary.Id, version);
            if (generation != _requestGeneration || loaded is null) return;
            _templateSummary = summary;
            _template = loaded;
            _currentName = $"{summary.Name}@{loaded.Version}";
        }
        catch (HttpRequestException)
        {
            // Reads as "not found" below: the template, or that version of it, could not be read.
        }
        finally
        {
            if (generation == _requestGeneration) _loading = false;
        }
    }

    private async Task PeekAsync(string name)
    {
        var generation = Interlocked.Increment(ref _requestGeneration);
        ClearTarget();
        _currentName = name;
        _loading = true;
        StateHasChanged();
        try
        {
            // Resolve by name within the same project, then fetch the full definition (list DTO may omit YAML).
            var page = await Api.Pipelines.GetPipelinesAsync(pageSize: 100, search: name, projectId: ProjectId);
            if (generation != _requestGeneration) return;
            var match = page.Items.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                var full = await Api.Pipelines.GetPipelineAsync(match.Id) ?? match;
                if (generation == _requestGeneration) _current = full;
            }
        }
        catch (HttpRequestException)
        {
            if (generation == _requestGeneration) _current = null;
        }
        finally
        {
            if (generation == _requestGeneration) _loading = false;
        }
    }

    private async Task CopyAsync()
    {
        if (string.IsNullOrEmpty(CurrentYaml)) return;
        await Js.InvokeVoidAsync("Aetheus.copyToClipboard", CurrentYaml);
        Toast.Success("Copied");
    }
}
