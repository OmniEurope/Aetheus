// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.MailboxUi;

/// <summary>Recette R1-17: the integrated mailbox page, locked until the password is typed, then folders, list and reader.</summary>
public sealed class MailboxViewTests : BunitContext
{
    private const string Base = "api/servers/7/mail/accounts/3/mailbox";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public MailboxViewTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers/7/mail/client-settings",
            new MailClientSettingsDto { Host = "mail.example.org", ImapPort = 993, SubmissionPort = 587 });
    }

    private static MailboxAccessDto Access(bool unlocked) => new()
    {
        AccountId = 3,
        Email = "me@example.org",
        Username = "me@example.org",
        Host = "mail.example.org",
        ImapPort = 993,
        SubmissionPort = 587,
        IsUnlocked = unlocked,
        UnlockedUntil = unlocked ? new DateTime(2026, 10, 10, 9, 0, 0) : null
    };

    private void UnlockedMailbox()
    {
        _handler.SetJsonResponse(HttpMethod.Get, Base, Access(unlocked: true));
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/folders", new List<MailboxFolderDto>
        {
            new() { Path = "INBOX", Name = "INBOX", Role = MailboxFolderRole.Inbox, Total = 2, Unread = 1 },
            new() { Path = "Trash", Name = "Trash", Role = MailboxFolderRole.Trash },
            new() { Path = "Invoices", Name = "Invoices", Role = MailboxFolderRole.Other }
        });
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/messages?", new PaginatedResult<MailboxMessageSummaryDto>
        {
            Items =
            [
                new() { Uid = 5, From = "Alice <alice@example.com>", Subject = "Planning", IsRead = false },
                new() { Uid = 4, From = "bob@example.com", Subject = "Lunch", IsRead = true, HasAttachments = true }
            ],
            TotalCount = 2,
            Page = 1,
            PageSize = 25
        });
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/messages/5", new MailboxMessageDto
        {
            Uid = 5,
            Folder = "INBOX",
            From = "Alice <alice@example.com>",
            To = "me@example.org",
            Subject = "Planning",
            Html = "<p class=\"probe\">Hello</p>",
            Text = "Hello",
            IsRead = true,
            BlockedRemoteImages = 2
        });
    }

    private IRenderedComponent<MailboxView> RenderView() =>
        Render<MailboxView>(parameters => parameters.Add(view => view.ServerId, 7).Add(view => view.AccountId, 3));

    [Fact]
    public void A_locked_mailbox_offers_to_unlock_and_reads_nothing()
    {
        _handler.SetJsonResponse(HttpMethod.Get, Base, Access(unlocked: false));

        var cut = RenderView();

        cut.WaitForAssertion(() => Assert.Contains("MailboxLocked", cut.Find(".mailbox-locked").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("/folders", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".mailbox-folders"));
        Assert.Contains("mail.example.org", cut.Find(".mail-client-settings").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unlock_opens_the_password_dialog_without_focus_and_then_loads_the_folders()
    {
        _handler.SetJsonResponse(HttpMethod.Get, Base, Access(unlocked: false));
        var cut = RenderView();
        cut.WaitForElement(".mailbox-locked");
        UnlockedMailbox();
        _dialog.OpenResult = Access(unlocked: true);

        await cut.InvokeAsync(() => cut.Instance.UnlockAsync());

        Assert.Equal(typeof(MailboxUnlockDialog), _dialog.LastComponent);
        Assert.Equal((7, 3), ((int)_dialog.LastParameters!["ServerId"]!, (int)_dialog.LastParameters["AccountId"]!));
        cut.WaitForAssertion(() => Assert.Contains("Enum_MailboxFolderRole_Inbox", cut.Find(".mailbox-folders").TextContent, StringComparison.Ordinal));
        Assert.Contains("Invoices", cut.Find(".mailbox-folders").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlocked_mailbox_lists_the_inbox_messages()
    {
        UnlockedMailbox();

        var cut = RenderView();

        cut.WaitForAssertion(() => Assert.Contains("Planning", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Lunch", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("messages?folder=INBOX", StringComparison.Ordinal));
        Assert.Contains("MailboxUnlockedUntil", cut.Find(".mailbox-header").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_a_message_shows_the_sanitized_html_and_the_blocked_images_notice()
    {
        UnlockedMailbox();
        var cut = RenderView();
        cut.WaitForAssertion(() => Assert.Contains("Planning", cut.Markup, StringComparison.Ordinal));

        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new MailboxMessageSummaryDto { Uid = 5 }));
        cut.Render();

        cut.WaitForAssertion(() => Assert.Contains("<p class=\"probe\">Hello</p>", cut.Find("iframe.mailbox-html").GetAttribute("srcdoc"), StringComparison.Ordinal));
        var frame = cut.Find("iframe.mailbox-html");
        Assert.Equal("allow-popups allow-popups-to-escape-sandbox", frame.GetAttribute("sandbox"));
        Assert.DoesNotContain("allow-scripts", frame.GetAttribute("sandbox"), StringComparison.Ordinal);
        Assert.Contains("MailboxImagesBlocked", cut.Find(".mailbox-images-blocked").TextContent, StringComparison.Ordinal);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("messages/5?folder=INBOX&loadImages=false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Show_images_asks_the_backend_to_fetch_them()
    {
        UnlockedMailbox();
        var cut = RenderView();
        cut.WaitForAssertion(() => Assert.Contains("Planning", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new MailboxMessageSummaryDto { Uid = 5 }));
        cut.Render();

        await cut.InvokeAsync(() => cut.Instance.LoadImagesAsync());

        Assert.Contains(_handler.Requests, request => request.Url.Contains("messages/5?folder=INBOX&loadImages=true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_conflict_puts_the_page_back_in_its_locked_state()
    {
        UnlockedMailbox();
        var cut = RenderView();
        cut.WaitForAssertion(() => Assert.Contains("Planning", cut.Markup, StringComparison.Ordinal));
        _handler.SetResponse(HttpMethod.Get, Base + "/folders", HttpStatusCode.Conflict);

        await cut.InvokeAsync(() => cut.Instance.RefreshAsync());
        cut.Render();

        cut.WaitForElement(".mailbox-locked");
        Assert.Empty(cut.FindAll(".mailbox-folders"));
    }

    [Fact]
    public async Task Lock_tells_the_backend_to_forget_the_password()
    {
        UnlockedMailbox();
        _handler.SetResponse(HttpMethod.Post, Base + "/lock", HttpStatusCode.NoContent);
        var cut = RenderView();
        cut.WaitForElement(".mailbox-folders");

        await cut.InvokeAsync(() => cut.Instance.LockAsync());
        cut.Render();

        Assert.Contains(_handler.Requests, request => request.Method == "POST" && request.Url.EndsWith(Base + "/lock", StringComparison.Ordinal));
        cut.WaitForElement(".mailbox-locked");
    }

    [Fact]
    public async Task Delete_from_the_inbox_moves_to_trash_without_asking()
    {
        UnlockedMailbox();
        _handler.SetJsonResponse(HttpMethod.Delete, Base + "/messages/5", new MailboxDeleteResultDto { Permanent = false });
        var cut = RenderView();
        cut.WaitForAssertion(() => Assert.Contains("Planning", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new MailboxMessageSummaryDto { Uid = 5 }));
        cut.Render();

        await cut.InvokeAsync(() => cut.Instance.DeleteAsync());
        cut.Render();

        Assert.Null(_dialog.LastConfirmMessage);
        Assert.Contains(_handler.Requests, request => request.Method == "DELETE" && request.Url.Contains("messages/5?folder=INBOX", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".mailbox-reader"));
    }

    [Fact]
    public async Task Delete_from_the_trash_asks_first_and_a_refusal_sends_nothing()
    {
        UnlockedMailbox();
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/messages/5", new MailboxMessageDto { Uid = 5, Folder = "Trash", Text = "x" });
        var cut = RenderView();
        cut.WaitForElement(".mailbox-folders");
        await cut.InvokeAsync(() => cut.Instance.SelectFolderAsync("Trash"));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new MailboxMessageSummaryDto { Uid = 5 }));
        cut.Render();
        _dialog.ConfirmResult = false;

        await cut.InvokeAsync(() => cut.Instance.DeleteAsync());
        cut.Render();

        Assert.Equal("MailboxDeletePermanentlyConfirm", _dialog.LastConfirmMessage);
        Assert.True(_dialog.LastConfirmOptions!.Destructive);
        Assert.DoesNotContain(_handler.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task Reply_prepares_the_draft_then_opens_the_compose_dialog_without_focus()
    {
        UnlockedMailbox();
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/messages/5/draft", new MailboxDraftDto { To = "alice@example.com", Subject = "Re: Planning" });
        var cut = RenderView();
        cut.WaitForAssertion(() => Assert.Contains("Planning", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new MailboxMessageSummaryDto { Uid = 5 }));
        cut.Render();

        await cut.InvokeAsync(() => cut.Instance.ComposeAsync(MailboxComposeMode.Reply));

        Assert.Contains(_handler.Requests, request => request.Url.Contains("messages/5/draft?folder=INBOX&mode=Reply", StringComparison.Ordinal));
        Assert.Equal(typeof(MailboxComposeDialog), _dialog.LastComponent);
        Assert.Equal("Re: Planning", ((MailboxDraftDto)_dialog.LastParameters!["Draft"]!).Subject);
        Assert.Equal("INBOX", _dialog.LastParameters["SourceFolder"]);
        Assert.Equal(5u, _dialog.LastParameters["SourceUid"]);
    }

    [Fact]
    public void A_read_only_user_cannot_write_or_delete()
    {
        UnlockedMailbox();
        Services.GetRequiredService<PermissionService>().SetPermissions(
            [new EffectivePermissionDto { ResourceType = ResourceType.Server, Permission = Permission.Read }], false);

        var cut = RenderView();

        cut.WaitForElement(".mailbox-folders");
        var write = cut.FindAll(".mailbox-header button").Single(button => button.TextContent.Contains("MailboxCompose", StringComparison.Ordinal));
        Assert.True(write.HasAttribute("disabled"));
    }
}
