// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.MailboxUi;

/// <summary>F-FRONT-015: a message read answers after the reader moved on. The answer is for the folder and UID that
/// were asked, so it must not become the open message of another folder: delete, move and download act on the open
/// message, and would otherwise hit the same UID of the folder now shown.</summary>
public sealed class MailboxViewStaleReadTests : BunitContext
{
    private const string Base = "api/servers/7/mail/accounts/3/mailbox";
    private readonly BunitTestHelper.TestHandler _handler;

    public MailboxViewStaleReadTests()
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
            new() { Path = "INBOX", Name = "INBOX", Role = MailboxFolderRole.Inbox, Total = 1, Unread = 0 },
            new() { Path = "Archive", Name = "Archive", Role = MailboxFolderRole.Archive, Total = 1, Unread = 0 }
        });
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/messages?", new PaginatedResult<MailboxMessageSummaryDto>
        {
            Items = [new() { Uid = 5, From = "Alice <alice@example.com>", Subject = "Planning", IsRead = true }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
        _handler.SetJsonResponse(HttpMethod.Delete, Base + "/messages/5", new MailboxDeleteResultDto { Permanent = false });
    }

    private static MailboxMessageDto MessageFive(string folder) => new()
    {
        Uid = 5,
        Folder = folder,
        From = "Alice <alice@example.com>",
        To = "me@example.org",
        Subject = "Planning",
        Html = "<p>Hello</p>",
        Text = "Hello",
        IsRead = true
    };

    private TaskCompletionSource<MailboxMessageDto> HoldTheRead()
    {
        var gate = new TaskCompletionSource<MailboxMessageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, Base + "/messages/5", _ => gate.Task);
        return gate;
    }

    private IRenderedComponent<MailboxView> OpenMailbox()
    {
        var cut = Render<MailboxView>(parameters => parameters.Add(view => view.ServerId, 7).Add(view => view.AccountId, 3));
        cut.WaitForAssertion(() => Assert.Contains("Planning", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public async Task ASlowReadOfFolderA_AnsweringAfterFolderB_IsDropped_AndNothingCanActOnItsUidInFolderB()
    {
        var cut = OpenMailbox();
        var gate = HoldTheRead();
        var read = cut.InvokeAsync(() => cut.Instance.OpenAsync(new MailboxMessageSummaryDto { Uid = 5 }));
        await _handler.WhenGetAsync(Base + "/messages/5");

        await cut.InvokeAsync(() => cut.Instance.SelectFolderAsync("Archive"));
        gate.SetResult(MessageFive("INBOX"));
        await read;
        cut.Render();

        Assert.Empty(cut.FindComponents<MailboxReader>());
        await cut.InvokeAsync(() => cut.Instance.DeleteAsync());
        await cut.InvokeAsync(() => cut.Instance.DownloadAsync(new MailboxAttachmentDto { Index = 0, FileName = "a.txt" }));
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Method is "DELETE");
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Url.Contains("/attachments/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLatestReadWins_WhenAnEarlierReadOfTheSameFolderAnswersLast()
    {
        var cut = OpenMailbox();
        var gate = HoldTheRead();
        var slow = cut.InvokeAsync(() => cut.Instance.OpenAsync(new MailboxMessageSummaryDto { Uid = 5 }));
        await _handler.WhenGetAsync(Base + "/messages/5");
        _handler.SetJsonResponse(HttpMethod.Get, Base + "/messages/5", MessageFive("INBOX") with { Subject = "Second read" });

        await cut.InvokeAsync(() => cut.Instance.OpenAsync(new MailboxMessageSummaryDto { Uid = 5 }));
        gate.SetResult(MessageFive("INBOX") with { Subject = "First read" });
        await slow;
        cut.Render();

        var reader = Assert.Single(cut.FindComponents<MailboxReader>());
        Assert.Equal("Second read", reader.Instance.Message.Subject);
    }

    [Fact]
    public async Task TheReadOfTheShownFolder_StillOpensItsMessage()
    {
        var cut = OpenMailbox();
        var gate = HoldTheRead();
        var read = cut.InvokeAsync(() => cut.Instance.OpenAsync(new MailboxMessageSummaryDto { Uid = 5 }));
        await _handler.WhenGetAsync(Base + "/messages/5");

        gate.SetResult(MessageFive("INBOX"));
        await read;
        cut.Render();

        var reader = Assert.Single(cut.FindComponents<MailboxReader>());
        Assert.Equal("Planning", reader.Instance.Message.Subject);
    }
}
