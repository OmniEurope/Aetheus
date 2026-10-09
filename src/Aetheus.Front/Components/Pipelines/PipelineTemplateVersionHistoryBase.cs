// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public abstract class PipelineTemplateVersionHistoryBase : ComponentBase
{
    [Inject] protected ApiClient Api { get; set; } = default!;
    [Inject] protected IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int TemplateId { get; set; }

    protected List<PipelineTemplateVersionSummaryDto> _versions = [];
    protected int _versionCount;
    protected bool _versionsLoading;
    protected bool _versionsLoadFailed;
    protected int _originalVersion;
    protected int _modifiedVersion;
    protected string _originalYaml = string.Empty;
    protected string _modifiedYaml = string.Empty;
    protected bool _yamlLoading;
    protected bool _yamlLoadFailed;

    private int _latestVersion;
    private int _yamlGeneration;

    protected async Task InitializeComparisonAsync(int latestVersion)
    {
        _latestVersion = latestVersion;
        _originalVersion = Math.Max(1, latestVersion - 1);
        _modifiedVersion = Math.Max(1, latestVersion);
        if (latestVersion > 1)
            await LoadSelectedYamlAsync().ConfigureAwait(false);
    }

    /// <summary>Recette R2-124: one page of the versions export, with the grid's sort and column filters.</summary>
    protected Task<PaginatedResult<PipelineTemplateVersionSummaryDto>> ReadVersionsExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(10);
        var (sortBy, descending) = ResolveSort(args);
        return Api.PipelineTemplates.GetPipelineTemplateVersionsAsync(
            TemplateId, page, pageSize, sortBy, descending, filters: args.ToApiFilters());
    }

    protected async Task LoadVersionsAsync(GridLoadArgs args)
    {
        var pageSize = args.Top ?? 10;
        var page = ((args.Skip ?? 0) / pageSize) + 1;
        var (sortBy, descending) = ResolveSort(args);
        _versionsLoading = true;
        _versionsLoadFailed = false;
        try
        {
            var result = await Api.PipelineTemplates.GetPipelineTemplateVersionsAsync(
                TemplateId, page, pageSize, sortBy, descending, filters: args.ToApiFilters()).ConfigureAwait(false);
            _versions = result.Items;
            _versionCount = result.TotalCount;
        }
        catch (HttpRequestException)
        {
            _versionsLoadFailed = true;
        }
        finally
        {
            _versionsLoading = false;
        }
    }

    protected async Task OnOriginalVersionChanged(int version)
    {
        _originalVersion = Math.Clamp(version, 1, _latestVersion);
        await LoadSelectedYamlAsync().ConfigureAwait(false);
    }

    protected async Task OnModifiedVersionChanged(int version)
    {
        _modifiedVersion = Math.Clamp(version, 1, _latestVersion);
        await LoadSelectedYamlAsync().ConfigureAwait(false);
    }

    protected async Task LoadSelectedYamlAsync()
    {
        var generation = ++_yamlGeneration;
        _yamlLoading = true;
        _yamlLoadFailed = false;
        try
        {
            var originalTask = Api.PipelineTemplates.GetPipelineTemplateVersionAsync(TemplateId, _originalVersion);
            var modifiedTask = _modifiedVersion == _originalVersion
                ? originalTask
                : Api.PipelineTemplates.GetPipelineTemplateVersionAsync(TemplateId, _modifiedVersion);
            await Task.WhenAll(originalTask, modifiedTask).ConfigureAwait(false);
            if (generation != _yamlGeneration)
                return;

            var original = await originalTask.ConfigureAwait(false);
            var modified = await modifiedTask.ConfigureAwait(false);
            if (original is null || modified is null)
            {
                _yamlLoadFailed = true;
                return;
            }

            _originalYaml = original.YamlContent;
            _modifiedYaml = modified.YamlContent;
        }
        catch (HttpRequestException)
        {
            if (generation == _yamlGeneration)
                _yamlLoadFailed = true;
        }
        finally
        {
            if (generation == _yamlGeneration)
                _yamlLoading = false;
        }
    }

    private static (string SortBy, bool Descending) ResolveSort(GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy))
            return ("Version", true);

        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }
}
