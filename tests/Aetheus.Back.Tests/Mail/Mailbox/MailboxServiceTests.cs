// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Time.Testing;
using MimeKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R1-17: the mailbox service behind a fake IMAP/SMTP connector (no mail server is reached here).
/// </summary>
public sealed class MailboxServiceTests
{
    private const string Password = "typed-mailbox-password";
    private const string Email = "me@example.org";
    private static readonly MailboxCredentialKey s_key = new("Bearer:1", 7, 3);

    private readonly IMailboxRepository _repository = Substitute.For<IMailboxRepository>();
    private readonly IMailboxConnector _connector = Substitute.For<IMailboxConnector>();
    private readonly IMailboxSession _session = Substitute.For<IMailboxSession>();
    private readonly IMailboxRemoteImageFetcher _images = Substitute.For<IMailboxRemoteImageFetcher>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));
    private readonly MailboxCredentialStore _credentials;
    private readonly MailboxService _sut;

    public MailboxServiceTests()
    {
        _credentials = new MailboxCredentialStore(_time);
        _repository.FindAccountAsync(7, 3, Arg.Any<CancellationToken>()).Returns(new MailboxAccount(3, 7, Email, "mail.example.org"));
        _connector.OpenAsync(Arg.Any<MailboxEndpoint>(), Arg.Any<CancellationToken>()).Returns(_session);
        _sut = new MailboxService(_repository, _connector, _credentials, _images, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Unlocked() => _credentials.Store(s_key, Password);

    private static MailboxFolderInfo Folder(string path, MailboxFolderRole role) => new(path, path, role, 0, 0);

    private static MimeMessage Message(string? html = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.To.Add(new MailboxAddress(string.Empty, Email));
        message.Subject = "Hello";
        message.MessageId = "m1@example.com";
        message.Body = new BodyBuilder { TextBody = "Plain", HtmlBody = html }.ToMessageBody();
        return message;
    }

    [Fact]
    public async Task Unlock_logs_in_with_the_typed_password_and_holds_it_only_then()
    {
        var access = await _sut.UnlockAsync(s_key, Password, Ct);

        await _connector.Received(1).OpenAsync(
            Arg.Is<MailboxEndpoint>(endpoint => endpoint.Host == "mail.example.org" && endpoint.ImapPort == MailboxPorts.Imaps
                && endpoint.SubmissionPort == MailboxPorts.Submission && endpoint.Username == Email && endpoint.Password == Password),
            Arg.Any<CancellationToken>());
        await _session.Received(1).DisposeAsync();
        Assert.True(access.IsUnlocked);
        Assert.NotNull(access.UnlockedUntil);
        Assert.Equal(Password, _credentials.TryUse(s_key));
    }

    [Fact]
    public async Task A_refused_unlock_holds_nothing()
    {
        _connector.OpenAsync(Arg.Any<MailboxEndpoint>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConflictException("refused"));

        await Assert.ThrowsAsync<ConflictException>(() => _sut.UnlockAsync(s_key, "wrong", Ct));

        Assert.Null(_credentials.TryUse(s_key));
    }

    [Fact]
    public async Task A_locked_mailbox_is_refused_without_connecting()
    {
        await Assert.ThrowsAsync<ConflictException>(() => _sut.ListFoldersAsync(s_key, Ct));

        await _connector.DidNotReceiveWithAnyArgs().OpenAsync(default!, Ct);
    }

    [Fact]
    public async Task Access_says_locked_then_unlocked_and_lock_forgets()
    {
        Assert.False((await _sut.GetAccessAsync(s_key, Ct)).IsUnlocked);
        Unlocked();
        var access = await _sut.GetAccessAsync(s_key, Ct);
        Assert.True(access.IsUnlocked);
        Assert.Equal(Email, access.Username);
        Assert.Equal(MailboxPorts.Imaps, access.ImapPort);

        _sut.Lock(s_key);

        Assert.False((await _sut.GetAccessAsync(s_key, Ct)).IsUnlocked);
    }

    [Fact]
    public async Task A_login_refused_later_forgets_the_held_password()
    {
        Unlocked();
        _connector.OpenAsync(Arg.Any<MailboxEndpoint>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConflictException("password changed"));

        await Assert.ThrowsAsync<ConflictException>(() => _sut.ListFoldersAsync(s_key, Ct));

        Assert.Null(_credentials.TryUse(s_key));
    }

    [Fact]
    public async Task An_unknown_mailbox_is_not_found()
    {
        Unlocked();
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetAccessAsync(s_key with { AccountId = 99 }, Ct));
    }

    [Fact]
    public async Task Messages_are_paged_and_the_page_size_clamped()
    {
        Unlocked();
        _session.ListMessagesAsync("INBOX", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MailboxPage(250, [new MailboxSummary(9, "a", Email, "s", DateTimeOffset.UnixEpoch, false, true, 10)]));

        var page = await _sut.ListMessagesAsync(s_key, "INBOX", 3, 1000, Ct);

        await _session.Received(1).ListMessagesAsync("INBOX", 2 * MailboxLimits.MaxPageSize, MailboxLimits.MaxPageSize, Arg.Any<CancellationToken>());
        Assert.Equal(250, page.TotalCount);
        Assert.Equal(MailboxLimits.MaxPageSize, page.PageSize);
        Assert.True(Assert.Single(page.Items).HasAttachments);
    }

    [Fact]
    public async Task Reading_marks_the_message_read_and_withholds_remote_images()
    {
        Unlocked();
        _session.GetMessageAsync("INBOX", 5u, Arg.Any<CancellationToken>())
            .Returns(new MailboxFetchedMessage(5, Message("<p>Hi</p><img src=\"https://t.example/p.gif\"><script>x()</script>"), false));

        var message = await _sut.GetMessageAsync(s_key, "INBOX", 5, loadRemoteImages: false, Ct);

        await _session.Received(1).SetSeenAsync("INBOX", 5u, true, Arg.Any<CancellationToken>());
        await _images.DidNotReceiveWithAnyArgs().FetchAsync(default!, Ct);
        Assert.True(message.IsRead);
        Assert.Equal(1, message.BlockedRemoteImages);
        Assert.DoesNotContain("script", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("t.example", message.Html, StringComparison.Ordinal);
        Assert.Equal("Plain", message.Text);
    }

    [Fact]
    public async Task Load_images_fetches_them_through_the_fetcher_and_inlines_them()
    {
        Unlocked();
        const string url = "https://cdn.example/a.png";
        _session.GetMessageAsync("INBOX", 5u, Arg.Any<CancellationToken>())
            .Returns(new MailboxFetchedMessage(5, Message($"<img src=\"{url}\">"), true));
        _images.FetchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { [url] = "data:image/png;base64,AAAA" });

        var message = await _sut.GetMessageAsync(s_key, "INBOX", 5, loadRemoteImages: true, Ct);

        await _images.Received(1).FetchAsync(Arg.Is<IReadOnlyList<string>>(urls => urls.Single() == url), Arg.Any<CancellationToken>());
        await _session.DidNotReceiveWithAnyArgs().SetSeenAsync(default!, default, default, Ct);
        Assert.Contains("data:image/png;base64,AAAA", message.Html, StringComparison.Ordinal);
        Assert.Equal(0, message.BlockedRemoteImages);
    }

    [Fact]
    public async Task A_missing_message_is_not_found()
    {
        Unlocked();
        _session.GetMessageAsync("INBOX", 5u, Arg.Any<CancellationToken>()).Returns((MailboxFetchedMessage?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetMessageAsync(s_key, "INBOX", 5, false, Ct));
    }

    [Fact]
    public async Task Delete_moves_to_trash_and_erases_only_from_trash()
    {
        Unlocked();
        _session.ListFoldersAsync(Arg.Any<CancellationToken>())
            .Returns([Folder("INBOX", MailboxFolderRole.Inbox), Folder("Trash", MailboxFolderRole.Trash)]);

        var moved = await _sut.DeleteAsync(s_key, "INBOX", 5, Ct);
        var erased = await _sut.DeleteAsync(s_key, "Trash", 6, Ct);

        Assert.False(moved.Permanent);
        await _session.Received(1).MoveAsync("INBOX", 5u, "Trash", Arg.Any<CancellationToken>());
        Assert.True(erased.Permanent);
        await _session.Received(1).DeletePermanentlyAsync("Trash", 6u, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_creates_the_trash_folder_when_the_mailbox_has_none()
    {
        Unlocked();
        _session.ListFoldersAsync(Arg.Any<CancellationToken>()).Returns([Folder("INBOX", MailboxFolderRole.Inbox)]);
        _session.CreateFolderAsync("Trash", Arg.Any<CancellationToken>()).Returns("Trash");

        await _sut.DeleteAsync(s_key, "INBOX", 5, Ct);

        await _session.Received(1).MoveAsync("INBOX", 5u, "Trash", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_submits_as_the_mailbox_then_files_a_copy_in_sent()
    {
        Unlocked();
        _session.GetMessageAsync("INBOX", 5u, Arg.Any<CancellationToken>()).Returns(new MailboxFetchedMessage(5, Message(), true));
        _session.ListFoldersAsync(Arg.Any<CancellationToken>()).Returns([Folder("Sent", MailboxFolderRole.Sent)]);
        var request = new MailboxSendRequest
        {
            Mode = MailboxComposeMode.Reply,
            To = "alice@example.com",
            Subject = "Re: Hello",
            Body = "Thanks",
            SourceFolder = "INBOX",
            SourceUid = 5
        };

        var result = await _sut.SendAsync(s_key, request, Ct);

        Assert.True(result.SavedToSent);
        await _connector.Received(1).SendAsync(
            Arg.Is<MailboxEndpoint>(endpoint => endpoint.Password == Password && endpoint.Username == Email),
            Arg.Is<MimeMessage>(message => message.InReplyTo == "m1@example.com" && message.From.Mailboxes.Single().Address == Email),
            Arg.Any<CancellationToken>());
        await _session.Received(1).AppendAsync("Sent", Arg.Any<MimeMessage>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_sent_copy_the_server_refuses_does_not_fail_the_send()
    {
        Unlocked();
        _session.ListFoldersAsync(Arg.Any<CancellationToken>()).Returns([Folder("Sent", MailboxFolderRole.Sent)]);
        _session.AppendAsync("Sent", Arg.Any<MimeMessage>(), true, Arg.Any<CancellationToken>())
            .ThrowsAsync(new BadRequestException("quota exceeded"));

        var result = await _sut.SendAsync(s_key, new MailboxSendRequest { To = "a@example.com", Subject = "s", Body = "b" }, Ct);

        Assert.False(result.SavedToSent);
        Assert.Equal("quota exceeded", result.SentCopyProblem);
        await _connector.Received(1).SendAsync(Arg.Any<MailboxEndpoint>(), Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Attachments_over_the_total_are_refused_before_the_mailbox_is_even_opened()
    {
        Unlocked();
        var tooMuch = new MailboxSendRequest
        {
            To = "a@example.com",
            Attachments =
            [
                new MailboxOutgoingAttachment { FileName = "a.bin", Content = new byte[MailboxLimits.MaxAttachmentBytes] },
                new MailboxOutgoingAttachment { FileName = "b.bin", Content = new byte[MailboxLimits.MaxAttachmentBytes] },
                new MailboxOutgoingAttachment { FileName = "c.bin", Content = [1] }
            ]
        };

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.SendAsync(s_key, tooMuch, Ct));

        await _connector.DidNotReceiveWithAnyArgs().OpenAsync(default!, Ct);
        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task The_attached_files_leave_with_the_message()
    {
        Unlocked();
        _session.ListFoldersAsync(Arg.Any<CancellationToken>()).Returns([Folder("Sent", MailboxFolderRole.Sent)]);
        var request = new MailboxSendRequest
        {
            To = "a@example.com",
            Subject = "s",
            Body = "b",
            Attachments = [new MailboxOutgoingAttachment { FileName = "notes.txt", Content = [1, 2, 3] }]
        };

        await _sut.SendAsync(s_key, request, Ct);

        await _connector.Received(1).SendAsync(
            Arg.Any<MailboxEndpoint>(),
            Arg.Is<MimeMessage>(message => message.Attachments.Single().ContentDisposition!.FileName == "notes.txt"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reply_without_its_source_is_refused_before_anything_leaves()
    {
        Unlocked();

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SendAsync(s_key, new MailboxSendRequest { Mode = MailboxComposeMode.Reply, To = "a@example.com" }, Ct));

        await _connector.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Create_folder_refuses_duplicates_and_hierarchy_characters()
    {
        Unlocked();
        _session.ListFoldersAsync(Arg.Any<CancellationToken>()).Returns([Folder("Projects", MailboxFolderRole.Other)]);
        _session.CreateFolderAsync("Invoices", Arg.Any<CancellationToken>()).Returns("Invoices");

        var created = await _sut.CreateFolderAsync(s_key, "  Invoices ", Ct);

        Assert.Equal("Invoices", created.Path);
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateFolderAsync(s_key, "projects", Ct));
        Assert.Throws<BadRequestException>(() => MailboxService.ValidFolderName("a/b"));
        Assert.Throws<BadRequestException>(() => MailboxService.ValidFolderName("a.b"));
        Assert.Throws<BadRequestException>(() => MailboxService.ValidFolderName("a*"));
        Assert.Throws<BadRequestException>(() => MailboxService.ValidFolderName(" "));
    }

    [Fact]
    public async Task An_attachment_is_returned_by_position()
    {
        Unlocked();
        var message = Message();
        var builder = new BodyBuilder { TextBody = "x" };
        builder.Attachments.Add("a.txt", [65, 66], new ContentType("text", "plain"));
        message.Body = builder.ToMessageBody();
        _session.GetMessageAsync("INBOX", 5u, Arg.Any<CancellationToken>()).Returns(new MailboxFetchedMessage(5, message, true));

        var attachment = await _sut.GetAttachmentAsync(s_key, "INBOX", 5, 0, Ct);

        Assert.Equal("a.txt", attachment.FileName);
        Assert.Equal([65, 66], attachment.Content);
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetAttachmentAsync(s_key, "INBOX", 5, 1, Ct));
    }

    [Fact]
    public async Task A_message_over_the_size_cap_is_refused_without_being_downloaded()
    {
        Unlocked();
        _session.GetMessageSizeAsync("INBOX", 5u, Arg.Any<CancellationToken>()).Returns(MailboxService.MaxMessageBytes + 1);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.GetMessageAsync(s_key, "INBOX", 5, false, Ct));
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.GetAttachmentAsync(s_key, "INBOX", 5, 0, Ct));

        await _session.DidNotReceiveWithAnyArgs().GetMessageAsync(default!, default, Ct);
        await _session.DidNotReceiveWithAnyArgs().SetSeenAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task A_message_at_the_size_cap_is_downloaded()
    {
        Unlocked();
        _session.GetMessageSizeAsync("INBOX", 5u, Arg.Any<CancellationToken>()).Returns(MailboxService.MaxMessageBytes);
        _session.GetMessageAsync("INBOX", 5u, Arg.Any<CancellationToken>()).Returns(new MailboxFetchedMessage(5, Message(), true));

        var message = await _sut.GetMessageAsync(s_key, "INBOX", 5, false, Ct);

        Assert.True(message.IsRead);
        await _session.Received(1).GetMessageAsync("INBOX", 5u, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Client_settings_name_the_mail_host_and_the_real_ports()
    {
        _repository.GetMailHostAsync(7, Arg.Any<CancellationToken>()).Returns("mail.example.org");

        var settings = await _sut.GetClientSettingsAsync(7, Ct);

        Assert.Equal(("mail.example.org", 993, 587), (settings.Host, settings.ImapPort, settings.SubmissionPort));
    }
}
