// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.MailboxUi;

/// <summary>R-10 on the mailbox message list: marking the open message read or unread, and deleting it, change the row
/// at once, before the folder is read again and without the grid's blocking bar. The mailbox answers these actions with
/// a bare success, not the row, so the row is the listed one with the change applied locally.</summary>
public sealed class MailboxViewRowChangesTests : BunitContext
{
    private const string Base = "api/servers/7/mail/accounts/3/mailbox";
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private readonly BunitTestHelper.TestHandler _handler;

    public MailboxViewRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers/7/mail/client-settings",
            new MailClientSettingsDto { Host = "mail.example.org", ImapPort = 993, SubmissionPort = 587 });
        _handler.SetJsonResponse(HttpMethod.Get, Base, new MailboxAccessDto
        {
            AccountId = 3,
            Email = "me@example.org",
            Username = "me@example.org",
            Host = "mail.example.org",
            ImapPort = 993,
            SubmissionPort = 587,
            IsUnlocked = true,
            UnlockedUntil = new DateTime(2026, 10, 10, 9, 0, 0)
        });
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/folders", new List<MailboxFolderDto>
        {
            new() { Path = "INBOX", Name = "INBOX", Role = MailboxFolderRole.Inbox, Total = 2, Unread = 1 }
        });
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/messages?", Messages(unreadFive: false, withFour: true));
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/messages/5", new MailboxMessageDto
        {
            Uid = 5,
            Folder = "INBOX",
            From = "Alice <alice@example.com>",
            To = "me@example.org",
            Subject = "Planning",
            Html = "<p>Hello</p>",
            Text = "Hello",
            IsRead = true
        });
    }

    private static PaginatedResult<MailboxMessageSummaryDto> Messages(bool unreadFive, bool withFour)
    {
        var items = new List<MailboxMessageSummaryDto> { new() { Uid = 5, From = "Alice <alice@example.com>", Subject = "Planning", IsRead = !unreadFive } };
        if (withFour) items.Add(new() { Uid = 4, From = "bob@example.com", Subject = "Lunch", IsRead = true });
        return new() { Items = items, TotalCount = items.Count, Page = 1, PageSize = 25 };
    }

    private async Task<(IRenderedComponent<MailboxView> Cut, AetheusDataGrid<MailboxMessageSummaryDto> Grid)> OpenMessageFiveAsync()
    {
        var cut = Render<MailboxView>(parameters => parameters.Add(view => view.ServerId, 7).Add(view => view.AccountId, 3));
        cut.WaitForAssertion(() => Assert.Contains("Planning", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new MailboxMessageSummaryDto { Uid = 5 }));
        return (cut, cut.FindComponent<AetheusDataGrid<MailboxMessageSummaryDto>>().Instance);
    }

    private TaskCompletionSource<PaginatedResult<MailboxMessageSummaryDto>> HoldTheNextRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<MailboxMessageSummaryDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, Base + "/messages?", _ => gate.Task);
        return gate;
    }

    [Fact]
    public async Task MarkingTheOpenMessageUnread_ShowsTheRowUnreadAtOnce_WhileTheReadBehindItIsStillRunning()
    {
        var (cut, grid) = await OpenMessageFiveAsync();
        Assert.Empty(cut.FindAll(".mailbox-row--unread"));
        _handler.SetJsonResponse(HttpMethod.Put, Base + "/messages/5/read", true);
        var gate = HoldTheNextRead();

        await cut.InvokeAsync(() => cut.Instance.ToggleReadAsync());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mailbox-row--unread")));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Messages(unreadFive: true, withFour: true));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.NotEmpty(cut.FindAll(".mailbox-row--unread"));
    }

    [Fact]
    public async Task ADeletedMessage_LeavesTheListAtOnce_WhileTheReadBehindItIsStillRunning()
    {
        var (cut, grid) = await OpenMessageFiveAsync();
        _handler.SetJsonResponse(HttpMethod.Delete, Base + "/messages/5", new MailboxDeleteResultDto { Permanent = false });
        var gate = HoldTheNextRead();

        await cut.InvokeAsync(() => cut.Instance.DeleteAsync());

        cut.WaitForAssertion(() => Assert.DoesNotContain("Planning</", cut.Find(".mailbox-messages").InnerHtml, StringComparison.Ordinal));
        Assert.Contains("Lunch", cut.Markup, StringComparison.Ordinal);
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Messages(unreadFive: false, withFour: true) with { Items = [.. Messages(false, true).Items.Skip(1)], TotalCount = 1 });
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("Lunch", cut.Markup, StringComparison.Ordinal);
    }
}
