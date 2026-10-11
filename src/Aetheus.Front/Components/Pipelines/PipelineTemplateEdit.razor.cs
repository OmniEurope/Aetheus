// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineTemplateEdit
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    [Parameter] public int? Id { get; set; }

    private PipelineTemplateDto? _template;
    private TemplateFormModel _model = new();
    private bool _loading;
    private bool _loadFailed;

    // A template that does not exist and a request that failed are two different answers to the
    // reader: the first is final, the second is worth retrying. Every other detail page in the app
    // says "<entity> not found."; this one used to report a load failure for both.
    private bool _notFound;
    private bool _saving;
    private int? _loadedId = int.MinValue;
    private bool _isNew => Id is null or 0;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedId == Id) return;
        _loadedId = Id;
        Breadcrumb.Set(new BreadcrumbItem(L["PipelineTemplates"], "/templates"),
            new BreadcrumbItem(_isNew ? L["NewTemplate"] : L["EditTemplate"]));

        if (_isNew)
        {
            _template = null;
            _model = new TemplateFormModel
            {
                Category = "Pipeline",
                ChangelogEntry = L["InitialTemplateVersion"],
                YamlContent = """
                    name: reusable-pipeline
                    trigger: manual
                    stages:
                      - name: Validate
                        jobs:
                          - name: validate
                            agent: default
                            steps:
                              - name: Check repository
                                shell: git status --short
                    """
            };
            return;
        }

        _loading = true;
        _loadFailed = false;
        _notFound = false;
        try
        {
            _template = await Api.PipelineTemplates.GetPipelineTemplateAsync(Id!.Value);
            _notFound = _template is null;
            if (_template is not null)
            {
                _model = new TemplateFormModel
                {
                    Name = _template.Name,
                    Description = _template.Description,
                    Category = _template.Category,
                    YamlContent = _template.YamlContent
                };
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { _notFound = true; }
        catch (HttpRequestException) { _loadFailed = true; }
        finally { _loading = false; }
    }

    private async Task OnSubmitAsync()
    {
        if (_saving) return;
        _saving = true;
        try
        {
            PipelineTemplateDto? saved;
            if (_isNew)
            {
                saved = await Api.PipelineTemplates.CreatePipelineTemplateAsync(new CreatePipelineTemplateRequest
                {
                    Name = _model.Name,
                    Description = _model.Description,
                    Category = _model.Category,
                    YamlContent = _model.YamlContent,
                    ChangelogEntry = _model.ChangelogEntry
                });
            }
            else
            {
                saved = await Api.PipelineTemplates.UpdatePipelineTemplateAsync(Id!.Value, new UpdatePipelineTemplateRequest
                {
                    Name = _model.Name,
                    Description = _model.Description,
                    Category = _model.Category,
                    YamlContent = _model.YamlContent,
                    ChangelogEntry = _model.ChangelogEntry
                });
            }

            if (saved is null)
            {
                Toast.Error("Error", "SaveFailed");
                return;
            }
            Toast.Success(_isNew ? "Created" : "Saved", saved.Name);
            Nav.NavigateTo($"/templates/{saved.Id}");
        }
        catch (HttpRequestException) { Toast.Error("Error", "SaveFailed"); }
        finally { _saving = false; }
    }

    private void Cancel() => Nav.NavigateTo("/templates");
    private void ViewHistory() => Nav.NavigateTo($"/templates/{Id}/versions");
    private void UseTemplate()
    {
        if (_template is not null) Nav.NavigateTo(PipelineTemplateUseRoute.For(_template.Id, _template.Name));
    }

    private sealed class TemplateFormModel
    {
        [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
        [StringLength(500)] public string Description { get; set; } = string.Empty;
        [Required, StringLength(50)] public string Category { get; set; } = string.Empty;
        [Required, StringLength(50_000)] public string YamlContent { get; set; } = string.Empty;
        [Required, StringLength(500)] public string ChangelogEntry { get; set; } = string.Empty;
    }
}
