// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;

namespace Aetheus.Front.Components.Mail;

/// <summary>
/// Recette R1-17: the integrated mailbox. Every call goes through the backend, which uses the password
/// the reader typed; a 409 means it is no longer held, and the page goes back to its locked state.
/// </summary>
public partial class MailboxView : IDisposable
{
    private const string Inbox = "INBOX";

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter, EditorRequired] public int AccountId { get; set; }

    private AetheusDataGrid<MailboxMessageSummaryDto>? _grid;
    private MailboxAccessDto? _access;
    private List<MailboxFolderDto> _folders = [];
    private List<MailboxMessageSummaryDto> _messages = [];
    private MailboxMessageDto? _message;
    private string _folder = Inbox;
    private int _total;
    private bool _loading;
    private bool _loadingImages;
    private bool _failed;
    private bool _canWrite;

    internal bool Unlocked => _access?.IsUnlocked == true;
    internal bool CanWrite => _canWrite;

    protected override async Task OnInitializedAsync()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        try
        {
            var access = await Api.Mailbox.GetAccessAsync(ServerId, AccountId);
            _failed = !access.Ok;
            _access = access.Value;
            if (Unlocked) await LoadFoldersAsync();
        }
        catch (HttpRequestException)
        {
            // An expired session is redirected to the login page; the page says the read failed meanwhile.
            _failed = true;
        }
    }

    public void Dispose()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        GC.SuppressFinalize(this);
    }

    internal async Task UnlockAsync()
    {
        var result = await Dialog.OpenAsync<MailboxUnlockDialog>(
            string.Format(L["MailboxUnlockTitle"], _access?.Email ?? string.Empty),
            new Dictionary<string, object?> { ["ServerId"] = ServerId, ["AccountId"] = AccountId },
            new OmniDialogOptions { Width = "32rem", AutoFocusFirstElement = false });
        if (result is not MailboxAccessDto access) return;
        _access = access;
        _folder = Inbox;
        _message = null;
        await LoadFoldersAsync();
        StateHasChanged();
    }

    internal async Task LockAsync()
    {
        await Api.Mailbox.LockAsync(ServerId, AccountId);
        BecomeLocked();
    }

    internal async Task RefreshAsync()
    {
        await LoadFoldersAsync();
        if (_grid is not null) await _grid.Reload();
    }

    internal async Task SelectFolderAsync(string path)
    {
        _folder = path;
        _message = null;
        if (_grid is not null) await _grid.GoToPage(0, forceReload: true);
    }

    internal async Task LoadMessagesAsync(GridLoadArgs args)
    {
        if (!Unlocked) return;
        var (page, pageSize) = args.ToPageRequest(25);
        _loading = true;
        try
        {
            var result = await Api.Mailbox.GetMessagesAsync(ServerId, AccountId, _folder, page, pageSize);
            if (Handle(result) is { } messages)
            {
                _messages = messages.Items;
                _total = messages.TotalCount;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The export reads the folder page by page from the backend, as the grid does.</summary>
    internal async Task<PaginatedResult<MailboxMessageSummaryDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(MailboxLimits.MaxPageSize);
        return Handle(await Api.Mailbox.GetMessagesAsync(ServerId, AccountId, _folder, page, pageSize)) ?? new();
    }

    internal async Task OpenAsync(MailboxMessageSummaryDto summary) => await ReadAsync(summary.Uid, loadImages: false);

    internal async Task LoadImagesAsync()
    {
        if (_message is null) return;
        _loadingImages = true;
        try
        {
            await ReadAsync(_message.Uid, loadImages: true);
        }
        finally
        {
            _loadingImages = false;
        }
    }

    internal async Task ToggleReadAsync()
    {
        if (_message is null) return;
        var isRead = !_message.IsRead;
        var result = await Api.Mailbox.SetReadAsync(ServerId, AccountId, _folder, _message.Uid, isRead);
        Handle(result);
        if (!result.Ok) return;
        Toast.Success(isRead ? "MailboxMarkRead" : "MailboxMarkUnread");
        _message = _message with { IsRead = isRead };
        await RefreshAsync();
    }

    internal async Task DeleteAsync()
    {
        if (_message is null) return;
        var inTrash = _folders.Any(folder => folder.Path == _folder && folder.Role == MailboxFolderRole.Trash);
        if (inTrash)
        {
            var confirmed = await Dialog.Confirm(L["MailboxDeletePermanentlyConfirm"].Value, L["Delete"].Value,
                new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
            if (confirmed != true) return;
        }
        if (Handle(await Api.Mailbox.DeleteAsync(ServerId, AccountId, _folder, _message.Uid)) is not { } deleted) return;
        Toast.Success(deleted.Permanent ? "MailboxDeletedPermanently" : "MailboxMovedToTrash");
        _message = null;
        await RefreshAsync();
    }

    internal async Task ComposeAsync(MailboxComposeMode mode)
    {
        var draft = new MailboxDraftDto();
        if (mode != MailboxComposeMode.New)
        {
            if (_message is null) return;
            var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (Handle(await Api.Mailbox.GetDraftAsync(ServerId, AccountId, _folder, _message.Uid, mode, language)) is not { } prepared)
                return;
            draft = prepared;
        }
        var title = mode switch
        {
            MailboxComposeMode.Reply => L["MailboxReply"],
            MailboxComposeMode.ReplyAll => L["MailboxReplyAll"],
            MailboxComposeMode.Forward => L["MailboxForward"],
            _ => L["MailboxNewMessage"]
        };
        var result = await Dialog.OpenAsync<MailboxComposeDialog>(title, new Dictionary<string, object?>
        {
            ["ServerId"] = ServerId,
            ["AccountId"] = AccountId,
            ["From"] = _access?.Email ?? string.Empty,
            ["Mode"] = mode,
            ["Draft"] = draft,
            ["SourceFolder"] = mode == MailboxComposeMode.New ? null : _folder,
            ["SourceUid"] = mode == MailboxComposeMode.New ? null : _message?.Uid
        }, new OmniDialogOptions { Width = "48rem", AutoFocusFirstElement = false });
        if (result is not MailboxSendResultDto sent) return;
        if (sent.SavedToSent) Toast.Success("MailboxSent");
        else Toast.Warning("MailboxSent", "MailboxSentCopyFailed", sent.SentCopyProblem ?? string.Empty);
        await LoadFoldersAsync();
    }

    internal async Task CreateFolderAsync()
    {
        var result = await Dialog.OpenAsync<MailboxFolderDialog>(L["MailboxNewFolder"], new Dictionary<string, object?>(),
            new OmniDialogOptions { Width = "28rem", AutoFocusFirstElement = false });
        if (result is not string name || name.Length == 0) return;
        if (Handle(await Api.Mailbox.CreateFolderAsync(ServerId, AccountId, new MailboxCreateFolderRequest { Name = name })) is null) return;
        Toast.Success("MailboxFolderCreated");
        await LoadFoldersAsync();
    }

    internal async Task DownloadAsync(MailboxAttachmentDto attachment)
    {
        if (_message is null) return;
        var bytes = await Api.Mailbox.GetAttachmentAsync(ServerId, AccountId, _folder, _message.Uid, attachment.Index);
        if (bytes is null) return;
        await Js.InvokeVoidAsync("downloadFileFromBytes", bytes, attachment.FileName, attachment.ContentType);
    }

    internal void BackToMail() => Nav.NavigateTo($"/servers/{ServerId}/mail?tab=accounts");

    internal string FolderLabel(MailboxFolderDto folder) =>
        folder.Role is MailboxFolderRole.Other ? folder.Name : L[$"Enum_MailboxFolderRole_{folder.Role}"];

    internal static string FolderIcon(MailboxFolderRole role) => role switch
    {
        MailboxFolderRole.Inbox => "inbox",
        MailboxFolderRole.Sent => "send",
        MailboxFolderRole.Drafts => "edit",
        MailboxFolderRole.Trash => "delete",
        MailboxFolderRole.Junk => "report_problem",
        MailboxFolderRole.Archive => "inventory",
        _ => "folder"
    };

    private async Task ReadAsync(uint uid, bool loadImages)
    {
        if (Handle(await Api.Mailbox.GetMessageAsync(ServerId, AccountId, _folder, uid, loadImages)) is not { } message) return;
        _message = message;
        var listed = _messages.FindIndex(item => item.Uid == uid);
        if (listed >= 0 && !_messages[listed].IsRead)
        {
            _messages[listed] = _messages[listed] with { IsRead = true };
            await LoadFoldersAsync();
        }
    }

    private async Task LoadFoldersAsync()
    {
        if (Handle(await Api.Mailbox.GetFoldersAsync(ServerId, AccountId)) is not { } folders) return;
        _folders = folders;
        if (_folders.Count > 0 && _folders.All(folder => folder.Path != _folder))
            _folder = _folders.FirstOrDefault(folder => folder.Role == MailboxFolderRole.Inbox)?.Path ?? _folders[0].Path;
    }

    /// <summary>The value of a successful call; on a 409 the page goes back to its locked state.</summary>
    private T? Handle<T>(MailboxResult<T> result)
    {
        if (result.Locked) BecomeLocked();
        return result.Ok ? result.Value : default;
    }

    private void BecomeLocked()
    {
        if (_access is not null) _access = _access with { IsUnlocked = false, UnlockedUntil = null };
        _folders = [];
        _messages = [];
        _total = 0;
        _message = null;
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        _ = InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.Server, ServerId);
}
