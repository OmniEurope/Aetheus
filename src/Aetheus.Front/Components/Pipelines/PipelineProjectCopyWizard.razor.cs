// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R1-29, option 1: the setup wizard's "start from an existing project". The user names the new
/// project and its prefix, the project to start from and its prefix; the backend plans the copy
/// (names, verdict of each pipeline, structure of the libraries and vaults they use) and makes it.
/// Nothing here ever carries a value of a variable or a secret: the plan and the result name keys only,
/// and the result says which ones are now empty and what the new project still lacks.
/// </summary>
public partial class PipelineProjectCopyWizard
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<ProjectDto> Projects { get; set; } = [];

    /// <summary>The new project, when the wizard was opened from it.</summary>
    [Parameter] public int? InitialProjectId { get; set; }

    [Parameter] public EventCallback OnCancel { get; set; }

    private const int PlanStep = 1;
    private const int SummaryStep = 2;

    private List<WizardStep> _steps = [];
    private int _currentStep;
    private string? _planSignature;
    private bool _initialized;

    internal int? TargetProjectId { get; private set; }
    internal string Prefix { get; private set; } = string.Empty;
    internal int? SourceProjectId { get; private set; }
    internal string SourcePrefix { get; private set; } = string.Empty;
    internal PipelineProjectCopyPlanDto? Plan { get; private set; }
    internal bool PlanLoading { get; private set; }
    internal bool PlanFailed { get; private set; }
    internal HashSet<int> Selected { get; } = [];
    internal PipelineProjectCopyResultDto? Result { get; private set; }
    internal string? CopyError { get; private set; }
    internal bool Saving { get; private set; }
    internal bool ReadinessLoading { get; private set; }

    internal ProjectDto? TargetProject => Projects.FirstOrDefault(project => project.Id == TargetProjectId);
    internal ProjectDto? SourceProject => Projects.FirstOrDefault(project => project.Id == SourceProjectId);

    /// <summary>Every project except the new one: a project is not copied into itself.</summary>
    internal IEnumerable<ProjectDto> SourceProjects => Projects.Where(project => project.Id != TargetProjectId);

    internal IReadOnlyList<PipelineCopyItemDto> SelectedPipelines =>
        Plan?.Pipelines.Where(item => Selected.Contains(item.SourcePipelineId)).ToList() ?? [];

    private bool CanAdvance => !Saving && _currentStep switch
    {
        0 => TargetProjectId.HasValue && SourceProjectId.HasValue && TargetProjectId != SourceProjectId
            && IsPrefix(Prefix) && IsPrefix(SourcePrefix),
        PlanStep => !PlanLoading && Selected.Count > 0,
        _ => Result is not null || Selected.Count > 0
    };

    protected override void OnParametersSet()
    {
        if (_initialized) return;
        _initialized = true;
        TargetProjectId = InitialProjectId;
        if (TargetProject is { } target) Prefix = PipelineSetupWizard.Slug(target.Name);
        RefreshSteps();
    }

    /// <summary>The same shape the request's validation accepts: lowercase words joined by hyphens.</summary>
    internal static bool IsPrefix(string value) =>
        value.Length is > 0 and <= 60 && Regex.IsMatch(value, "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    internal void SetTargetProject(int? projectId)
    {
        TargetProjectId = projectId;
        if (TargetProject is { } target) Prefix = PipelineSetupWizard.Slug(target.Name);
        if (SourceProjectId == projectId) SourceProjectId = null;
        RefreshSteps();
    }

    internal void SetPrefix(string value)
    {
        Prefix = value.Trim();
        RefreshSteps();
    }

    internal void SetSourceProject(int? projectId)
    {
        SourceProjectId = projectId;
        if (SourceProject is { } source) SourcePrefix = PipelineSetupWizard.Slug(source.Name);
        RefreshSteps();
    }

    internal void SetSourcePrefix(string value)
    {
        SourcePrefix = value.Trim();
        RefreshSteps();
    }

    internal void Toggle(int sourcePipelineId, bool selected)
    {
        if (selected) Selected.Add(sourcePipelineId);
        else Selected.Remove(sourcePipelineId);
        RefreshSteps();
    }

    private void RefreshSteps()
    {
        _steps =
        [
            new() { Title = L["Project"], Content = RenderStep(0) },
            new() { Title = L["PipelineCopyStepPipelines"], Content = RenderStep(PlanStep) },
            new() { Title = L["WizardStepSummary"], Content = RenderStep(SummaryStep) }
        ];
        StateHasChanged();
    }

    private RenderFragment RenderStep(int step) => builder =>
    {
        builder.OpenComponent<PipelineProjectCopyStep>(0);
        builder.AddAttribute(1, nameof(PipelineProjectCopyStep.Step), step);
        builder.AddAttribute(2, nameof(PipelineProjectCopyStep.Flow), this);
        builder.AddAttribute(3, nameof(PipelineProjectCopyStep.OnReadinessChanged),
            EventCallback.Factory.Create(this, RecheckReadinessAsync));
        builder.CloseComponent();
    };

    private async Task OnStepChanged(int step)
    {
        _currentStep = step;
        RefreshSteps();
        if (step == PlanStep) await LoadPlanAsync();
    }

    private PipelineProjectCopyRequest BuildRequest(IEnumerable<int> pipelineIds) => new()
    {
        SourceProjectId = SourceProjectId ?? 0,
        TargetProjectId = TargetProjectId ?? 0,
        Prefix = Prefix,
        SourcePrefix = SourcePrefix,
        TargetBranch = TargetProject?.DefaultBranch,
        PipelineIds = [.. pipelineIds]
    };

    /// <summary>The plan of every pipeline of the source project, read once per choice of projects and
    /// prefixes: stepping back and forth keeps the user's selection.</summary>
    private async Task LoadPlanAsync()
    {
        var signature = $"{TargetProjectId}:{Prefix}:{SourceProjectId}:{SourcePrefix}";
        if (signature == _planSignature && Plan is not null) return;
        _planSignature = signature;
        Plan = null;
        PlanFailed = false;
        PlanLoading = true;
        Selected.Clear();
        RefreshSteps();
        try
        {
            Plan = await Api.Pipelines.PlanProjectCopyAsync(BuildRequest([]));
            PlanFailed = Plan is null;
            foreach (var item in Plan?.Pipelines.Where(item => item.Status != PipelineCopyStatus.NotCopied) ?? [])
                Selected.Add(item.SourcePipelineId);
        }
        catch (HttpRequestException)
        {
            PlanFailed = true;
        }
        finally
        {
            if (PlanFailed) _planSignature = null;
            PlanLoading = false;
            RefreshSteps();
        }
    }

    private async Task FinishAsync()
    {
        if (Result is not null)
        {
            Nav.NavigateTo($"/projects/{TargetProjectId}/pipelines");
            return;
        }
        if (!CanAdvance) return;
        Saving = true;
        CopyError = null;
        RefreshSteps();
        try
        {
            var outcome = await Api.Pipelines.CopyProjectPipelinesAsync(BuildRequest(Selected));
            if (outcome.Value is { } result)
            {
                Result = result;
                Toast.Success(string.Format(L["PipelineCopyDone"],
                    result.Plan.Pipelines.Count(item => item.CreatedPipelineId is not null)));
            }
            else
            {
                CopyError = outcome.Error?.Message is { Length: > 0 } message ? message : L["OperationFailed"].Value;
            }
        }
        catch (HttpRequestException)
        {
            CopyError = L["OperationFailed"].Value;
        }
        finally
        {
            Saving = false;
            RefreshSteps();
        }
    }

    /// <summary>After a library, vault or repository was created from the result, the verdict is read
    /// again for the new project's pipelines, which now exist.</summary>
    private async Task RecheckReadinessAsync()
    {
        if (Result is null || TargetProjectId is not { } projectId) return;
        ReadinessLoading = true;
        RefreshSteps();
        try
        {
            if (await Api.Pipelines.GetProjectReadinessAsync(projectId) is { } readiness)
                Result = Result with { Readiness = readiness };
        }
        catch (HttpRequestException)
        {
            // The previous verdict stays shown; nothing new is claimed.
        }
        finally
        {
            ReadinessLoading = false;
            RefreshSteps();
        }
    }
}
