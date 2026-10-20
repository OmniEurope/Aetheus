// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Mail;

/// <summary>The reading pane of one message and its actions; the host does the work.</summary>
public partial class MailboxReader
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public MailboxMessageDto Message { get; set; } = new();
    [Parameter] public bool CanWrite { get; set; }
    [Parameter] public bool LoadingImages { get; set; }
    [Parameter] public EventCallback<MailboxComposeMode> OnCompose { get; set; }
    [Parameter] public EventCallback OnToggleRead { get; set; }
    [Parameter] public EventCallback OnDelete { get; set; }
    [Parameter] public EventCallback OnLoadImages { get; set; }
    [Parameter] public EventCallback<MailboxAttachmentDto> OnDownload { get; set; }

    internal string Subject => string.IsNullOrWhiteSpace(Message.Subject) ? "-" : Message.Subject;

    /// <summary>The sanitized HTML as the frame's own document; links target a new tab.</summary>
    internal string Document =>
        $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><base target=\"_blank\"></head><body>{Message.Html}</body></html>";
}
