// SPDX-License-Identifier: EUPL-1.2

using System.Globalization;

namespace Aetheus.Front.Components.Mail;

/// <summary>Composes and sends a message with attachments, closing with <see
/// cref="MailboxSendResultDto"/> after sending.</summary>
public partial class MailboxComposeDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter, EditorRequired] public int AccountId { get; set; }
    [Parameter, EditorRequired] public string From { get; set; } = string.Empty;
    [Parameter] public MailboxComposeMode Mode { get; set; }
    [Parameter] public MailboxDraftDto Draft { get; set; } = new();
    [Parameter] public string? SourceFolder { get; set; }
    [Parameter] public uint? SourceUid { get; set; }

    private MailboxComposeModel _model = new();
    private string? _recipientError;
    private bool _busy;
    private bool _closed;

    internal IReadOnlyList<MailboxAttachmentDto> ForwardedAttachments => Draft.ForwardedAttachments;

    // The files the writer attached, each list entry of the upload field paired with the bytes read
    // for it. The bytes are only held to be sent: never opened, previewed or rendered.
    private List<(OmniUploadFile Entry, MailboxOutgoingAttachment File)> _attachments = [];
    private List<MailboxOutgoingAttachment> _pending = [];

    internal IReadOnlyList<OmniUploadFile> AttachedFiles => _attachments.ConvertAll(attachment => attachment.Entry);

    internal IReadOnlyList<MailboxOutgoingAttachment> Attachments => _attachments.ConvertAll(attachment => attachment.File);

    private string AttachmentsHint => string.Format(CultureInfo.CurrentCulture, L["MailboxAttachmentsHint"],
        MailboxLimits.MaxAttachmentCount, MailboxLimits.MaxAttachmentBytes / (1024 * 1024), MailboxLimits.MaxAttachmentsTotalBytes / (1024 * 1024));

    /// <summary>
    /// Reads an accepted selection (the field already checked the count and each file's size) and refuses
    /// it whole when, with the files already attached, it would pass the total the server accepts.
    /// </summary>
    internal async Task ReadAttachmentsAsync(OmniUploadRequest request)
    {
        _pending = [];
        long total = _attachments.Sum(attachment => (long)attachment.File.Content.Length);
        if (total + request.Files.Sum(file => file.Size) > MailboxLimits.MaxAttachmentsTotalBytes)
        {
            request.Reject(string.Format(CultureInfo.CurrentCulture, L["MailboxAttachmentsTooLarge"], MailboxLimits.MaxAttachmentsTotalBytes / (1024 * 1024)));
            return;
        }

        var read = new List<MailboxOutgoingAttachment>(request.Files.Count);
        for (var index = 0; index < request.Files.Count; index++)
        {
            var file = request.Files[index];
            await using var stream = request.OpenReadStream(file);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, request.CancellationToken);
            read.Add(new MailboxOutgoingAttachment { FileName = file.Name, Content = buffer.ToArray() });
            request.ReportProgress(100d * (index + 1) / request.Files.Count);
        }
        _pending = read;
    }

    /// <summary>
    /// The field's new list: entries it kept stay paired with their bytes, a removed entry drops its bytes,
    /// and new entries take, in order, the files <see cref="ReadAttachmentsAsync"/> just read.
    /// </summary>
    internal void OnAttachedFilesChanged(IReadOnlyList<OmniUploadFile> files)
    {
        var next = new List<(OmniUploadFile Entry, MailboxOutgoingAttachment File)>(files.Count);
        var pending = new Queue<MailboxOutgoingAttachment>(_pending);
        foreach (var entry in files)
        {
            var kept = _attachments.FindIndex(attachment => ReferenceEquals(attachment.Entry, entry));
            if (kept >= 0) next.Add(_attachments[kept]);
            else if (pending.TryDequeue(out var file)) next.Add((entry, file));
        }
        _attachments = next;
        _pending = [];
    }

    // OmniDialog re-renders on every key press with the same parameters: the draft is copied once.
    protected override void OnInitialized() => _model = new MailboxComposeModel
    {
        To = Draft.To,
        Cc = Draft.Cc,
        Subject = Draft.Subject,
        Body = Draft.Body
    };

    internal async Task SendAsync()
    {
        if (_busy || _closed) return;
        _recipientError = null;
        if (string.IsNullOrWhiteSpace(_model.To) && string.IsNullOrWhiteSpace(_model.Cc) && string.IsNullOrWhiteSpace(_model.Bcc))
        {
            _recipientError = L["MailboxRecipientRequired"];
            return;
        }
        _busy = true;
        try
        {
            var replying = Mode != MailboxComposeMode.New;
            var result = await Api.Mailbox.SendAsync(ServerId, AccountId, new MailboxSendRequest
            {
                Mode = Mode,
                To = _model.To.Trim(),
                Cc = _model.Cc.Trim(),
                Bcc = _model.Bcc.Trim(),
                Subject = _model.Subject,
                Body = _model.Body,
                SourceFolder = replying ? SourceFolder : null,
                SourceUid = replying ? SourceUid : null,
                Attachments = [.. Attachments]
            });
            // A refusal is shown by the API error toast; the dialog stays open with what was typed.
            if (result is { Ok: true, Value: { } sent })
            {
                _closed = true;
                Dialog.Close(sent);
            }
        }
        finally
        {
            _busy = false;
        }
    }
}

public sealed class MailboxComposeModel
{
    [StringLength(MailboxLimits.MaxAddressListLength)]
    public string To { get; set; } = string.Empty;

    [StringLength(MailboxLimits.MaxAddressListLength)]
    public string Cc { get; set; } = string.Empty;

    [StringLength(MailboxLimits.MaxAddressListLength)]
    public string Bcc { get; set; } = string.Empty;

    [StringLength(1000)]
    public string Subject { get; set; } = string.Empty;

    [StringLength(MailboxLimits.MaxBodyLength)]
    public string Body { get; set; } = string.Empty;
}
