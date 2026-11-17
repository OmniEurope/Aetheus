// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Mail;

/// <summary>The reading pane of one message and its actions; the host does the work.</summary>
public partial class MailboxReader
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;

    [Parameter, EditorRequired] public MailboxMessageDto Message { get; set; } = new();
    [Parameter] public bool CanWrite { get; set; }
    [Parameter] public bool LoadingImages { get; set; }
    [Parameter] public EventCallback<MailboxComposeMode> OnCompose { get; set; }
    [Parameter] public EventCallback OnToggleRead { get; set; }
    [Parameter] public EventCallback OnDelete { get; set; }
    [Parameter] public EventCallback OnLoadImages { get; set; }
    [Parameter] public EventCallback<MailboxAttachmentDto> OnDownload { get; set; }

    internal string Subject => string.IsNullOrWhiteSpace(Message.Subject) ? "-" : Message.Subject;

    /// <summary>The absolute URL of the sanitized document the API serves for the frame, or null when the
    /// message has none (the plain text is shown instead).</summary>
    internal string? DocumentUrl => Message.DocumentPath is { Length: > 0 } path ? Api.Mailbox.DocumentUrl(path) : null;
}
