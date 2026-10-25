// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class CronJobDialog
{
    [Parameter] public string JobId { get; set; } = string.Empty;
    // No "root" default: root cron jobs are rejected at save (the agent-compromise→root escalation path).
    // Create starts blank so the user picks a non-root account; Edit shows the job's existing user.
    [Parameter] public string User { get; set; } = string.Empty;
    [Parameter] public string Schedule { get; set; } = string.Empty;
    [Parameter] public string Command { get; set; } = string.Empty;

    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    // S-UX-CJ7D: form model so the dialog gets inline localized validation (required / length).
    // The non-root rule stays a UI hint + backend rejection (not a hard annotation) so a blank user
    // surfaces "required" rather than silently passing to an opaque backend failure.
    private readonly CronForm _form = new();

    private sealed class CronForm
    {
        [Required]
        [StringLength(64)]
        public string User { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string Schedule { get; set; } = string.Empty;

        [Required]
        [StringLength(1000)]
        public string Command { get; set; } = string.Empty;
    }

    protected override void OnInitialized()
    {
        _form.User = User;
        _form.Schedule = Schedule;
        _form.Command = Command;
    }

    private (bool IsValid, string? NextRun, string? Error) ScheduleValidation =>
        CronSchedulePreview.ValidateCronExpression(_form.Schedule);

    private void Cancel() => Dialog.Close(null);

    // Submit fires only when DataAnnotations pass; the cron-syntax check is not an annotation, so
    // it is enforced here before closing the dialog.
    private void Save()
    {
        if (!ScheduleValidation.IsValid) return;
        Dialog.Close(new CronJobSaveRequest
        {
            Id = string.IsNullOrEmpty(JobId) ? null : JobId,
            User = _form.User,
            Schedule = _form.Schedule,
            Command = _form.Command
        });
    }
}
