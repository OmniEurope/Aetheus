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
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private QualityChangeNotifier QualityChange { get; set; } = default!;
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
        await SetProjectContextAsync(_finding);
        var occurrences = Api.Analysis.GetAnalysisFindingOccurrencesAsync(Id);
        var decisions = Api.Analysis.GetAnalysisFindingDecisionsAsync(Id);
        await Task.WhenAll(occurrences, decisions);
        _occurrences = await occurrences;
        _decisions = await decisions;
        UpdatePermission();
        await LoadExcerptAsync();
    }

    /// <summary>R-21: a finding belongs to one project, so the page stays in it: the trail reads
    /// Projects / project / Quality / finding, the menu keeps the project open and the back arrow leads to
    /// the project's Quality page.</summary>
    private async Task SetProjectContextAsync(AnalysisFindingDto finding)
    {
        string? projectName = null;
        try { projectName = (await Api.Projects.GetProjectDetailAsync(finding.ProjectId))?.Name; }
        catch (HttpRequestException) { projectName = null; }
        ProjectNav.Set(finding.ProjectId);
        Breadcrumb.SetProjectResource(
            finding.ProjectId, projectName, [],
            L["Projects"], L["Project"], L["AnalysisPortfolio"],
            "quality", "/analysis", new BreadcrumbItem(finding.Title));
    }

    /// <summary>Where the back arrow leads: the Quality page of the finding's project.</summary>
    private string? BackHref => _finding is null ? null : $"/projects/{_finding.ProjectId}/quality";

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

    /// <summary>R-24: the finding is one of the CVE watch, whose accepted risk lasts 30, 90 or 180 days.</summary>
    private bool IsCveWatchFinding =>
        _finding?.LatestOccurrence?.ScannerKey == AcceptedRiskDuration.CveWatchScannerKey
        || _occurrences.Any(occurrence => occurrence.ScannerKey == AcceptedRiskDuration.CveWatchScannerKey);

    /// <summary>The length is asked, instead of a free date, when accepting the risk of a CVE-watch finding.</summary>
    private bool AsksAcceptanceLength => IsCveWatchFinding && _decision.Status == AnalysisFindingStatus.Accepted;

    private List<OmniOption<int>>? _acceptanceOptions;
    private List<OmniOption<int>> AcceptanceOptions => _acceptanceOptions ??=
        [.. AcceptedRiskDuration.AllowedDays.Select(days => new OmniOption<int>(days, L["AnalysisAcceptanceDays", days].Value))];

    /// <summary>The acceptance in force, shown with how long it lasts. A finding that is open again (the acceptance
    /// expired or was reverted) has none.</summary>
    private AnalysisFindingDecisionDto? AcceptedDecision => _finding?.Status == AnalysisFindingStatus.Accepted
        ? _decisions.Where(decision => decision.RevokedAt is null && decision.Status == AnalysisFindingStatus.Accepted)
            .OrderByDescending(decision => decision.CreatedAt).FirstOrDefault()
        : null;

    /// <summary>The server stores UTC: a date read without a kind is UTC too, not local time.</summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    private async Task SaveDecisionAsync()
    {
        if (_finding is null) return;
        var asksLength = AsksAcceptanceLength;
        if (!asksLength && _decision.ExpiresAt.HasValue && _decision.ExpiresAt.Value <= DateTime.Now)
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
                ExpiresAt = asksLength ? null : _decision.ExpiresAt,
                AcceptanceDays = asksLength ? _decision.AcceptanceDays : null
            });
            if (outcome.Value is not null)
            {
                Toast.Success("Saved", "AnalysisDecisionSaved");
                QualityChange.Notify();
                await LoadAsync();
                _decision.Status = null;
                _decision.Reason = string.Empty;
                _decision.ExpiresAt = null;
                _decision.AcceptanceDays = AcceptedRiskDuration.DefaultDays;
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
            {
                QualityChange.Notify();
                await LoadAsync();
            }
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
            // The ticket is read by people in their language: severity and labels go through the localizer.
            Title = $"[{L.Localize(_finding.Severity)}] {_finding.Title}",
            Description = $"{L["AnalysisTicketHeading", _finding.Id]}\n\n{_finding.Message}\n\n"
                + $"{L["AnalysisTicketRuleLine", _finding.RuleId]}\n{L["AnalysisTicketFingerprintLine", _finding.Fingerprint]}",
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
        public int AcceptanceDays { get; set; } = AcceptedRiskDuration.DefaultDays;
    }
}
