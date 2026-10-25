// SPDX-License-Identifier: EUPL-1.2
using Markdig;
using Microsoft.AspNetCore.WebUtilities;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Analysis;

public partial class AnalysisFindingDetail : ComponentBase, IDisposable
{
    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Parameter] public int Id { get; set; }

    /// <summary>The pipeline run the reader comes from (the Gate tab of that run). The page then
    /// shows what that run analysed: its file, its line, its commit.</summary>
    [SupplyParameterFromQuery(Name = "run")] public int? Run { get; set; }

    /// <summary>The occurrence shown: the one of the run the reader comes from when the finding has one,
    /// else the latest.</summary>
    private AnalysisFindingOccurrenceDto? ShownOccurrence =>
        (Run is { } run ? _occurrences.FirstOrDefault(occurrence => occurrence.PipelineRunId == run) : null)
        ?? _finding?.LatestOccurrence;

    private AnalysisFindingDto? _finding;
    private List<AnalysisFindingOccurrenceDto> _occurrences = [];
    private List<AnalysisFindingDecisionDto> _decisions = [];
    private bool _loading = true;
    private bool _saving;
    private bool _reverting;
    private bool _canAdmin;
    private bool _canWrite;
    private bool _creatingTicket;
    private int? _createdTicketId;
    private bool _showPromptPreview;
    private SourceExcerpt? _excerpt;
    private readonly DecisionFormModel _decision = new();
    // Header filter texts, built once so the columns see the same delegate on every render.
    private Func<string, string>? _statusText;
    private Func<string, string>? _yesNoText;
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<AnalysisFindingStatus>(L);
    private Func<string, string> YesNoText => _yesNoText ??= GridFilterText.YesNo(L);
    private readonly AnalysisFindingStatus[] _decisionStatuses =
        [AnalysisFindingStatus.Accepted, AnalysisFindingStatus.FalsePositive, AnalysisFindingStatus.Mitigated];

    protected override async Task OnInitializedAsync()
    {
        Permissions.OnPermissionsChanged += PermissionsChanged;
        try { await LoadAsync(); }
        catch (HttpRequestException) { _finding = null; }
        _loading = false;
    }

    private async Task LoadAsync()
    {
        _finding = await Api.Analysis.GetAnalysisFindingAsync(Id);
        if (_finding is null) return;
        Breadcrumb.Set(
            new BreadcrumbItem(L["AnalysisPortfolio"], "/analysis"),
            new BreadcrumbItem(_finding.Title));
        var occurrences = Api.Analysis.GetAnalysisFindingOccurrencesAsync(Id);
        var decisions = Api.Analysis.GetAnalysisFindingDecisionsAsync(Id);
        await Task.WhenAll(occurrences, decisions);
        _occurrences = await occurrences;
        _decisions = await decisions;
        UpdatePermission();
        await LoadExcerptAsync();
    }

    /// <summary> The passage of the file under the location, read from the repository at the
    /// finding's commit (its branch when no commit was recorded). A file gone at that ref, a binary
    /// one or a line past its end shows no passage; the location and its link stay. </summary>
    private async Task LoadExcerptAsync()
    {
        _excerpt = null;
        if (ShownOccurrence is not { } source || !HasNavigableFile(source) || source.StartLine is not { } startLine)
            return;
        var fileRef = !string.IsNullOrWhiteSpace(source.CommitHash) ? source.CommitHash : source.BranchName;
        if (string.IsNullOrWhiteSpace(fileRef)) return;
        try
        {
            var blob = await Api.Git.GetGitBlobAsync(source.RepositoryId!.Value, fileRef, source.FilePath!.Trim().TrimStart('/'));
            if (blob is { IsBinary: false, Content: { } content })
                _excerpt = SourceExcerpt.Cut(content, startLine, source.EndLine);
        }
        catch (HttpRequestException)
        {
            _excerpt = null;
        }
    }

    private string ExcerptTitle(SourceExcerpt excerpt) =>
        string.Format(L["AnalysisSourceExcerptTitle"], excerpt.FirstLine, excerpt.LastLine);

    /// <summary>The branch's files in its repository, when the repository is known.</summary>
    private static string? BranchHref(AnalysisFindingOccurrenceDto source) =>
        source.RepositoryId is { } repositoryId && !string.IsNullOrWhiteSpace(source.BranchName)
            ? QueryHelpers.AddQueryString($"/git-repositories/{repositoryId}",
                new Dictionary<string, string?> { ["tab"] = "files", ["ref"] = source.BranchName })
            : null;

    /// <summary>The commit's page in its repository, when the repository is known.</summary>
    private static string? CommitHref(AnalysisFindingOccurrenceDto source) =>
        source.RepositoryId is { } repositoryId && !string.IsNullOrWhiteSpace(source.CommitHash)
            ? $"/git-repositories/{repositoryId}/commits/{Uri.EscapeDataString(source.CommitHash)}"
            : null;

    private void OpenSourceFile(AnalysisFindingOccurrenceDto source) => Navigation.NavigateTo(SourceFileHref(source));

    private async Task SaveDecisionAsync()
    {
        if (_finding is null) return;
        if (_decision.ExpiresAt.HasValue && _decision.ExpiresAt.Value <= DateTime.Now)
        {
            Toast.Error("Error", "AnalysisDecisionExpirationFuture");
            return;
        }

        _saving = true;
        try
        {
            var outcome = await Api.Analysis.CreateAnalysisFindingDecisionAsync(Id, new CreateAnalysisFindingDecisionRequest
            {
                Status = _decision.Status!.Value,
                Reason = _decision.Reason,
                ExpiresAt = _decision.ExpiresAt
            });
            if (outcome.Value is not null)
            {
                Toast.Success("Saved", "AnalysisDecisionSaved");
                await LoadAsync();
                _decision.Status = null;
                _decision.Reason = string.Empty;
                _decision.ExpiresAt = null;
            }
            else
            {
                Toast.Error("Error", "SaveFailed");
            }
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>A fixed finding has nothing left to decide (recette R1-35): its Decision tab is not offered.</summary>
    private bool ShowsDecisionTab => _finding?.Status != AnalysisFindingStatus.Fixed;

    private string[] TabSlugs => ShowsDecisionTab
        ? ["overview", "prompt", "occurrences", "decisions"]
        : ["overview", "prompt", "occurrences"];

    /// <summary>A decision is in force (not revoked) and the finding still carries it.</summary>
    private bool HasRevertibleDecision => _finding is not null
        && AnalysisFindingDecisionRevert.CanRevert(_finding.Status)
        && _decisions.Any(decision => decision.RevokedAt is null);

    private async Task RevertDecisionAsync()
    {
        _reverting = true;
        try
        {
            if (await new AnalysisFindingDecisionRevert(Api, Dialog, Toast, L).RevertAsync(Id))
                await LoadAsync();
        }
        finally
        {
            _reverting = false;
        }
    }

    private void OpenHelp()
    {
        if (_finding?.HelpUri is { } value
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
            Navigation.NavigateTo(uri.AbsoluteUri, forceLoad: true);
    }

    private string PromptMarkdown => _finding is null
        ? string.Empty
        : AnalysisAiPromptBuilder.BuildSingle(_finding, L);

    private string PromptHtml => Markdown.ToHtml(PromptMarkdown, MarkdownPipeline);
    private static string LocationRange(AnalysisFindingOccurrenceDto source) =>
        source.EndLine is { } endLine && source.StartLine is { } startLine && endLine > startLine
            ? $"{startLine}-{endLine}"
            : source.StartLine?.ToString() ?? string.Empty;

    private static bool HasNavigableFile(AnalysisFindingOccurrenceDto source)
    {
        var path = source.FilePath?.Trim();
        return source.RepositoryId.HasValue
            && !string.IsNullOrWhiteSpace(path)
            && path != "/"
            && !path.EndsWith('/');
    }

    private static string SourceFileHref(AnalysisFindingOccurrenceDto source)
    {
        var path = source.FilePath!.Trim().TrimStart('/');
        var fileRef = !string.IsNullOrWhiteSpace(source.CommitHash)
            ? source.CommitHash
            : source.BranchName;
        var query = new Dictionary<string, string?>
        {
            ["tab"] = "files",
            ["ref"] = fileRef,
            ["path"] = path,
            ["line"] = source.StartLine?.ToString()
        };
        return QueryHelpers.AddQueryString($"/git-repositories/{source.RepositoryId}", query);
    }

    private string DecisionStatusText(AnalysisFindingStatus status) => L[$"AnalysisFindingStatus{status}"];
    private string DecisionStatusDescription(AnalysisFindingStatus status) => L[$"AnalysisDecision{status}Description"];
    private static string DecisionStatusIcon(AnalysisFindingStatus status) => status switch
    {
        AnalysisFindingStatus.Accepted => "verified_user",
        AnalysisFindingStatus.FalsePositive => "fact_check",
        AnalysisFindingStatus.Mitigated => "health_and_safety",
        _ => "gavel"
    };

    private static OmniTone DecisionStatusBadgeStyle(AnalysisFindingStatus status) =>
        AnalysisPresentation.FindingStatusBadge(status);

    private void TogglePromptPreview() => _showPromptPreview = !_showPromptPreview;
    private Task CopyPromptAsync() => Clipboard.CopyAsync(PromptMarkdown, L["AnalysisPromptCopied"]);
    private Task DownloadPromptAsync() => Js.InvokeVoidAsync(
        "downloadFile", $"finding-{Id}-ai-prompt.md", PromptMarkdown, "text/markdown").AsTask();

    private async Task CreateTicketAsync()
    {
        if (_finding is null || !_canWrite || _creatingTicket || _createdTicketId.HasValue) return;
        _creatingTicket = true;
        var workItem = await Api.Pipelines.CreateWorkItemAsync(new CreateWorkItemRequest
        {
            ProjectId = _finding.ProjectId,
            Type = WorkItemType.Bug,
            Title = $"[{_finding.Severity}] {_finding.Title}",
            Description = $"Analysis finding #{_finding.Id}\n\n{_finding.Message}\n\nRule: {_finding.RuleId}\nFingerprint: {_finding.Fingerprint}",
            Status = WorkItemStatus.New,
            Priority = (int)_finding.Severity,
            Tags = ["analysis", _finding.Category.ToString().ToLowerInvariant()],
            ExternalId = $"analysis:{_finding.Id}",
            ExternalUrl = Navigation.Uri
        });
        if (workItem is not null)
        {
            _createdTicketId = workItem.Id;
            Toast.Success("Created", "AnalysisTicketCreated");
        }
        _creatingTicket = false;
    }

    private void PermissionsChanged()
    {
        UpdatePermission();
        _ = InvokeAsync(StateHasChanged);
    }

    private void UpdatePermission()
    {
        _canAdmin = _finding is not null && Permissions.CanAdmin(ResourceType.Project, _finding.ProjectId);
        _canWrite = _finding is not null && Permissions.CanWrite(ResourceType.Project, _finding.ProjectId);
    }

    private static OmniTone SeverityStyle(AnalysisSeverity severity) => severity switch
    {
        AnalysisSeverity.Critical => OmniTone.Danger,
        AnalysisSeverity.High => OmniTone.Warning,
        AnalysisSeverity.Medium => OmniTone.Accent,
        _ => OmniTone.Neutral
    };

    public void Dispose() => Permissions.OnPermissionsChanged -= PermissionsChanged;

    private sealed class DecisionFormModel
    {
        // Nothing is chosen until the reviewer picks an outcome.
        [Required(ErrorMessage = "AnalysisDecisionOutcomeRequired")] public AnalysisFindingStatus? Status { get; set; }
        [Required, StringLength(2000, MinimumLength = 10)] public string Reason { get; set; } = string.Empty;
        public DateTime? ExpiresAt { get; set; }
    }
}
