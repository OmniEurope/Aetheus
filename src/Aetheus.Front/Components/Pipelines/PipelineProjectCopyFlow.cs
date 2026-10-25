// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The steps and the state of copying the pipelines of an existing project (the Project, the
/// pipelines to take over and the summary), played by the setup wizard's own <see cref="WizardDialog"/>
/// once the "start from" choice of its first step says so. The flow plans and applies a project copy
/// from selected source and target names; plans and results carry variable and secret keys only,
/// identifying empty values without exposing their contents.</summary>
/// <param name="api">The backend client.</param>
/// <param name="nav">Where the finished copy sends the user.</param>
/// <param name="l">The localizer.</param>
/// <param name="toast">The success notification.</param>
/// <param name="projects">Every project, source and target.</param>
/// <param name="initialProjectId">The new project, when the wizard was opened from it.</param>
/// <param name="changed">Raised when the state changed and the host has to draw the steps again.</param>
public sealed class PipelineProjectCopyFlow(
    ApiClient api,
    NavigationManager nav,
    IStringLocalizer<AppStrings> l,
    NotifyHelper toast,
    IReadOnlyList<ProjectDto> projects,
    int? initialProjectId,
    Action changed) : IPipelineProjectCopyFlow
{
    private const int PlanStep = 1;
    private const int SummaryStep = 2;

    private string? _planSignature;
    private RenderFragment? _startFrom;

    public IReadOnlyList<ProjectDto> Projects => projects;
    public int? TargetProjectId { get; private set; } = initialProjectId;
    public string Prefix { get; private set; } = initialProjectId is { } id
        && projects.FirstOrDefault(project => project.Id == id) is { } target ? PipelinePrefix.FromName(target.Name) : string.Empty;
    public int? SourceProjectId { get; private set; }
    public string SourcePrefix { get; private set; } = string.Empty;
    public PipelineProjectCopyPlanDto? Plan { get; private set; }
    public bool PlanLoading { get; private set; }
    public bool PlanFailed { get; private set; }
    internal HashSet<int> Selected { get; } = [];
    IReadOnlySet<int> IPipelineProjectCopyFlow.Selected => Selected;
    public PipelineProjectCopyResultDto? Result { get; private set; }
    public string? CopyError { get; private set; }
    internal bool Saving { get; private set; }
    public bool ReadinessLoading { get; private set; }

    public ProjectDto? TargetProject => Projects.FirstOrDefault(project => project.Id == TargetProjectId);
    public ProjectDto? SourceProject => Projects.FirstOrDefault(project => project.Id == SourceProjectId);

    /// <summary>Every project except the new one: a project is not copied into itself.</summary>
    public IEnumerable<ProjectDto> SourceProjects => Projects.Where(project => project.Id != TargetProjectId);

    public IReadOnlyList<PipelineCopyItemDto> SelectedPipelines =>
        Plan?.Pipelines.Where(item => Selected.Contains(item.SourcePipelineId)).ToList() ?? [];

    /// <summary>The text of the last step's button: the copy, then the way to the new pipelines.</summary>
    public string FinishText => Result is null ? l["PipelineCopyFinish"] : l["PipelineCopyOpenPipelines"];

    /// <summary>Whether the step shown lets the user go on.</summary>
    public bool CanAdvance(int step) => !Saving && step switch
    {
        0 => TargetProjectId.HasValue && SourceProjectId.HasValue && TargetProjectId != SourceProjectId
            && IsPrefix(Prefix) && IsPrefix(SourcePrefix),
        PlanStep => !PlanLoading && Selected.Count > 0,
        _ => Result is not null || Selected.Count > 0
    };

    /// <summary>The same shape the request's validation accepts: lowercase words joined by hyphens.</summary>
    internal static bool IsPrefix(string value) =>
        value.Length is > 0 and <= 60 && Regex.IsMatch(value, "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public void SetTargetProject(int? projectId)
    {
        TargetProjectId = projectId;
        if (TargetProject is { } target) Prefix = PipelinePrefix.FromName(target.Name);
        if (SourceProjectId == projectId) SourceProjectId = null;
        changed();
    }

    public void SetPrefix(string value)
    {
        Prefix = value.Trim();
        changed();
    }

    public void SetSourceProject(int? projectId)
    {
        SourceProjectId = projectId;
        if (SourceProject is { } source) SourcePrefix = PipelinePrefix.FromName(source.Name);
        changed();
    }

    public void SetSourcePrefix(string value)
    {
        SourcePrefix = value.Trim();
        changed();
    }

    public void Toggle(int sourcePipelineId, bool selected)
    {
        if (selected) Selected.Add(sourcePipelineId);
        else Selected.Remove(sourcePipelineId);
        changed();
    }

    /// <summary>The wizard's steps for this mode; the first one opens with the "start from" choice.</summary>
    /// <param name="startFrom">The choice of the host, drawn at the top of the Project step.</param>
    public List<WizardStep> Steps(RenderFragment startFrom)
    {
        _startFrom = startFrom;
        return
        [
            new() { Title = l["Project"], Content = RenderStep(0) },
            new() { Title = l["PipelineCopyStepPipelines"], Content = RenderStep(PlanStep) },
            new() { Title = l["WizardStepSummary"], Content = RenderStep(SummaryStep) }
        ];
    }

    private RenderFragment RenderStep(int step) => builder =>
    {
        builder.OpenComponent<PipelineProjectCopyStep>(0);
        builder.AddAttribute(1, nameof(PipelineProjectCopyStep.Step), step);
        builder.AddAttribute(2, nameof(PipelineProjectCopyStep.Flow), this);
        builder.AddAttribute(3, nameof(PipelineProjectCopyStep.OnReadinessChanged),
            EventCallback.Factory.Create(this, RecheckReadinessAsync));
        builder.AddAttribute(4, nameof(PipelineProjectCopyStep.StartFrom), _startFrom);
        builder.CloseComponent();
    };

    /// <summary>Reaching the pipelines step reads the plan.</summary>
    public async Task OnStepChangedAsync(int step)
    {
        changed();
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
        changed();
        try
        {
            Plan = await api.Pipelines.PlanProjectCopyAsync(BuildRequest([]));
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
            changed();
        }
    }

    /// <summary>Copies the chosen pipelines; once copied, the same button opens the project's pipelines.</summary>
    public async Task FinishAsync(int step)
    {
        if (Result is not null)
        {
            nav.NavigateTo($"/projects/{TargetProjectId}/pipelines");
            return;
        }
        if (!CanAdvance(step)) return;
        Saving = true;
        CopyError = null;
        changed();
        try
        {
            var outcome = await api.Pipelines.CopyProjectPipelinesAsync(BuildRequest(Selected));
            if (outcome.Value is { } result)
            {
                Result = result;
                toast.Success(string.Format(l["PipelineCopyDone"],
                    result.Plan.Pipelines.Count(item => item.CreatedPipelineId is not null)));
            }
            else
            {
                CopyError = outcome.Error?.Message is { Length: > 0 } message ? message : l["OperationFailed"].Value;
            }
        }
        catch (HttpRequestException)
        {
            CopyError = l["OperationFailed"].Value;
        }
        finally
        {
            Saving = false;
            changed();
        }
    }

    /// <summary>After a library, vault or repository was created from the result, the verdict is read
    /// again for the new project's pipelines, which now exist.</summary>
    private async Task RecheckReadinessAsync()
    {
        if (Result is null || TargetProjectId is not { } projectId) return;
        ReadinessLoading = true;
        changed();
        try
        {
            if (await api.Pipelines.GetProjectReadinessAsync(projectId) is { } readiness)
                Result = Result with { Readiness = readiness };
        }
        catch (HttpRequestException)
        {
            // The previous verdict stays shown; nothing new is claimed.
        }
        finally
        {
            ReadinessLoading = false;
            changed();
        }
    }
}
