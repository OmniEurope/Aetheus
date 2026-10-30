// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Rspamd state, lifecycle, thresholds and training.</summary>
public partial class ServerMailSpamTab
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter, EditorRequired] public MailSpamFilterStateDto Spam { get; set; } = new();
    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter] public bool CanManage { get; set; }

    /// <summary>R-119: follows the task an action queues; the page is veiled until it ends.</summary>
    [Parameter] public ServerTaskFollower? Follower { get; set; }

    private bool _busy;

    internal static string Score(double? score) =>
        score?.ToString("0.##", CultureInfo.CurrentCulture) ?? "?";

    // R-119: the queued task is followed and the page veiled until it ends.
    private void Followed(MailTaskQueuedDto? queued, string title)
    {
        if (queued is not null) Follower?.Follow(title, queued.TaskId);
    }

    private async Task InstallAsync()
    {
        _busy = true;
        try
        {
            var queued = await Api.Mail.InstallSpamFilterAsync(ServerId);
            Followed(queued, L["MailSpamFilter"]);
            if (queued is not null) Toast.Success(L["TaskQueued"]);
            else Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RunAsync(MailAction action)
    {
        _busy = true;
        try
        {
            var queued = await Api.Mail.QueueMailActionAsync(ServerId, new MailActionRequest { Action = action });
            Followed(queued, L["MailSpamFilter"]);
            if (queued is not null) Toast.Success(L["TaskQueued"]);
            else Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task EditThresholdsAsync()
    {
        var model = new MailDialogModel
        {
            RejectScore = Spam.RejectScore ?? 15,
            AddHeaderScore = Spam.AddHeaderScore ?? 6,
            GreylistScore = Spam.GreylistScore ?? 4
        };
        if (await OpenAsync(MailDialogMode.SpamThresholds, L["MailSpamThresholds"], model) is not { } result) return;

        _busy = true;
        try
        {
            var queued = await Api.Mail.UpdateSpamFilterAsync(ServerId, new UpdateSpamFilterRequest
            {
                RejectScore = result.RejectScore,
                AddHeaderScore = result.AddHeaderScore,
                GreylistScore = result.GreylistScore
            });
            Followed(queued, L["MailSpamThresholds"]);
            if (queued is not null) Toast.Success(L["TaskQueued"]);
            else Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task LearnAsync()
    {
        if (await OpenAsync(MailDialogMode.LearnSpam, L["MailLearnTitle"], new MailDialogModel()) is not { } result) return;

        _busy = true;
        try
        {
            var queued = await Api.Mail.LearnSpamAsync(ServerId, new LearnSpamRequest { IsSpam = result.IsSpam, RawMessage = result.RawMessage });
            Followed(queued, L["MailLearnTitle"]);
            if (queued is not null) Toast.Success(L["TaskQueued"]);
            else Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<MailDialogModel?> OpenAsync(MailDialogMode mode, string title, MailDialogModel model) =>
        await Dialog.OpenAsync<MailOperationDialog>(title,
            new Dictionary<string, object?> { { "Mode", mode }, { "Model", model } },
            new OmniDialogOptions { Width = mode == MailDialogMode.LearnSpam ? "42rem" : "32rem", AutoFocusFirstElement = false })
        as MailDialogModel;
}
