// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R1-41 / R3-50: an administrator opens a mailbox without its password through the server's Dovecot
/// master user; anyone else is refused before any connection. The opening is audited by address, the master
/// password is never audited, and the mailbox then works through the master login (behind a fake connector).
/// </summary>
public sealed class MailboxAdministratorAccessTests
{
    private const string Master = "generated-master-password-0123456789ABCD";
    private const string Email = "me@example.org";
    private static readonly MailboxCredentialKey s_key = new("Bearer:1", 7, 3);

    private readonly IMailboxRepository _repository = Substitute.For<IMailboxRepository>();
    private readonly IMailboxConnector _connector = Substitute.For<IMailboxConnector>();
    private readonly IMailboxSession _session = Substitute.For<IMailboxSession>();
    private readonly IMailMasterSecrets _secrets = Substitute.For<IMailMasterSecrets>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero));
    private readonly MailboxCredentialStore _credentials;
    private readonly MailboxAdministratorAccess _sut;

    public MailboxAdministratorAccessTests()
    {
        _credentials = new MailboxCredentialStore(_time);
        _repository.FindAccountAsync(7, 3, Arg.Any<CancellationToken>()).Returns(new MailboxAccount(3, 7, Email, "mail.example.org"));
        _connector.OpenAsync(Arg.Any<MailboxEndpoint>(), Arg.Any<CancellationToken>()).Returns(_session);
        _secrets.ReadAsync(7, Arg.Any<CancellationToken>()).Returns(Master);
        _sut = new MailboxAdministratorAccess(_repository, _connector, _credentials, _secrets, _audit);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reader_who_is_not_an_administrator_is_refused_without_reading_the_secret_or_connecting()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.OpenAsync(s_key, isAdministrator: false, Ct));

        Assert.Empty(_secrets.ReceivedCalls());
        Assert.Empty(_connector.ReceivedCalls());
        Assert.Empty(_audit.ReceivedCalls());
        Assert.Null(_credentials.TryUse(s_key));
    }

    [Fact]
    public async Task An_administrator_logs_in_as_the_master_user_and_the_opening_is_audited_without_the_password()
    {
        var access = await _sut.OpenAsync(s_key, isAdministrator: true, Ct);

        await _connector.Received(1).OpenAsync(
            Arg.Is<MailboxEndpoint>(endpoint => endpoint.Username == "me@example.org*aetheus-master" && endpoint.Password == Master
                && endpoint.Host == "mail.example.org" && endpoint.ImapPort == MailboxPorts.Imaps),
            Arg.Any<CancellationToken>());
        Assert.True(access.IsUnlocked);
        Assert.True(access.OpenedAsAdministrator);
        Assert.Equal(Email, access.Username);
        await _audit.Received(1).LogAsync("MailboxAdministratorOpened", "Mail", 7, Email, Arg.Any<CancellationToken>());
        Assert.DoesNotContain(_audit.ReceivedCalls(), call => call.GetArguments().OfType<string>().Any(a => a.Contains(Master)));
        Assert.True(_credentials.IsAdministratorAccess(s_key));
    }

    [Fact]
    public async Task The_mailbox_then_reads_and_sends_through_the_master_login()
    {
        await _sut.OpenAsync(s_key, isAdministrator: true, Ct);
        var mailbox = new MailboxService(_repository, _connector, _credentials, Substitute.For<IMailboxRemoteImageFetcher>(), _time);
        _session.ListFoldersAsync(Arg.Any<CancellationToken>()).Returns(new List<MailboxFolderInfo>());
        _connector.ClearReceivedCalls();

        await mailbox.ListFoldersAsync(s_key, Ct);
        var access = await mailbox.GetAccessAsync(s_key, Ct);

        await _connector.Received(1).OpenAsync(
            Arg.Is<MailboxEndpoint>(endpoint => endpoint.Username == "me@example.org*aetheus-master" && endpoint.Password == Master),
            Arg.Any<CancellationToken>());
        Assert.True(access.OpenedAsAdministrator);
    }

    [Fact]
    public async Task No_prepared_access_is_refused_plainly_and_holds_nothing()
    {
        _secrets.ReadAsync(7, Arg.Any<CancellationToken>()).Returns((string?)null);

        var refused = await Assert.ThrowsAsync<BadRequestException>(() => _sut.OpenAsync(s_key, isAdministrator: true, Ct));

        Assert.Contains("Prepare administrator access", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_connector.ReceivedCalls());
        Assert.Empty(_audit.ReceivedCalls());
    }

    [Fact]
    public async Task A_master_login_the_server_refuses_is_not_a_locked_mailbox_and_holds_nothing()
    {
        _connector.OpenAsync(Arg.Any<MailboxEndpoint>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConflictException("AUTHENTICATE failed"));

        var refused = await Assert.ThrowsAsync<BadRequestException>(() => _sut.OpenAsync(s_key, isAdministrator: true, Ct));

        Assert.DoesNotContain(Master, refused.Message, StringComparison.Ordinal);
        Assert.Null(_credentials.TryUse(s_key));
        Assert.Empty(_audit.ReceivedCalls());
    }

    [Fact]
    public async Task Preparation_is_known_from_the_stored_row_without_decrypting_it()
    {
        _repository.GetEncryptedSecretAsync(MailMasterAccess.SecretKey(7), Arg.Any<CancellationToken>()).Returns("cipher");

        Assert.True(await _sut.IsPreparedAsync(7, Ct));
        Assert.False(await _sut.IsPreparedAsync(8, Ct));
        Assert.Empty(_secrets.ReceivedCalls());
    }

    [Fact]
    public void The_held_credential_keeps_the_password_out_of_its_text()
    {
        var held = new MailboxHeldCredential(Master, MailMasterAccess.Login(Email));

        Assert.DoesNotContain(Master, held.ToString(), StringComparison.Ordinal);
        Assert.Contains("me@example.org*aetheus-master", held.ToString(), StringComparison.Ordinal);
    }
}
