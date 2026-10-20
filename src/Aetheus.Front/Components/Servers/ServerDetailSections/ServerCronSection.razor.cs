// SPDX-License-Identifier: EUPL-1.2
using Cronos;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerCronSection : ServerActionSectionBase
{
    [Inject] private ApiClient Api { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    private string _jobSearch = string.Empty;

    private CronDataDto Cron => Server.Cron;

    // Each heartbeat brings a new job list; jobs that were not there read bold.
    private OmniDataGrid<CronJobDto>? _grid;
    private readonly LiveGridRows _liveRows = new();

    protected override Task OnParametersSetAsync() => _liveRows.ObserveAsync(_grid, Server.Cron, ServerId);

    internal static (bool IsValid, string? NextRun, string? Error) ValidateCronExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return (false, null, null);
        try
        {
            // Standard 5-field only: /etc/cron.d entries are 5-field, and a 6-field (seconds) schedule is
            // rejected at save (CronValidation.IsValidScheduleSyntax), so the preview must mirror that.
            var cron = CronExpression.Parse(expression, CronFormat.Standard);
            var next = cron.GetNextOccurrence(DateTime.UtcNow);
            return (true, next?.ToLocalTime().ToString("g"), null);
        }
        catch (CronFormatException ex)
        {
            return (false, null, ex.Message);
        }
    }

    private List<CronJobDto> FilteredJobs => string.IsNullOrWhiteSpace(_jobSearch)
        ? Cron.Jobs
        : Cron.Jobs.Where(j =>
            j.Command.Contains(_jobSearch, StringComparison.OrdinalIgnoreCase) ||
            j.User.Contains(_jobSearch, StringComparison.OrdinalIgnoreCase) ||
            j.Schedule.Contains(_jobSearch, StringComparison.OrdinalIgnoreCase)).ToList();

    private async Task OpenCreateDialog()
    {
        // SITE-OFFLINE-001: refused before the user fills a job the agent cannot receive.
        if (BlockedByOfflineAgent()) return;
        var result = await Dialog.OpenAsync<CronJobDialog>(
            L["NewCronJob"],
            new Dictionary<string, object?>
            {
                { "JobId", string.Empty },
                { "User", string.Empty },
                { "Schedule", string.Empty },
                { "Command", string.Empty }
            },
            new OmniDialogOptions { Width = "32rem", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });

        if (result is CronJobSaveRequest request)
            await SaveJobAsync(request);
    }

    // Recette R3-35: clicking a job edits it, when its Edit button would (agent online, not a system job).
    private Task OpenFromRowAsync(CronJobDto job) =>
        AgentOffline || job.IsSystem ? Task.CompletedTask : OpenEditDialog(job);

    private async Task OpenEditDialog(CronJobDto job)
    {
        if (BlockedByOfflineAgent()) return;
        var result = await Dialog.OpenAsync<CronJobDialog>(
            L["EditCronJob"],
            new Dictionary<string, object?>
            {
                { "JobId", job.Id },
                { "User", job.User },
                { "Schedule", job.Schedule },
                { "Command", job.Command }
            },
            new OmniDialogOptions { Width = "32rem", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });

        if (result is CronJobSaveRequest request)
            await SaveJobAsync(request);
    }

    private async Task SaveJobAsync(CronJobSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Schedule) || string.IsNullOrWhiteSpace(request.Command))
        {
            Toast.Error(L["Error"], L["CronJobInvalid"]);
            return;
        }

        if (BlockedByOfflineAgent()) return;
        _actionRunning = true;
        // Recette R3-37: the follower shows what becomes of the task, in place of a lone "Task queued".
        _follower?.Expect(L["Save"]);
        var success = await Api.ServerTools.SaveCronJobAsync(ServerId, request);
        if (success)
            Toast.Success(L["TaskQueued"]);
        else
        {
            _follower?.Forget();
            Toast.Error(L["Error"], L["ActionFailed"]);
        }
        _actionRunning = false;
    }

    private async Task DeleteJobAsync(CronJobDto job)
    {
        if (BlockedByOfflineAgent()) return;
        _actionRunning = true;
        _follower?.Expect(L["DeleteCronJob"]);
        var success = await Api.ServerTools.DeleteCronJobAsync(ServerId, new CronJobDeleteRequest { Id = job.Id, User = job.User });
        if (success)
            Toast.Success(L["TaskQueued"]);
        else
        {
            _follower?.Forget();
            Toast.Error(L["Error"], L["ActionFailed"]);
        }
        _actionRunning = false;
    }
}
