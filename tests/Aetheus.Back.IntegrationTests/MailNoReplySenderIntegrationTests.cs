// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Aetheus.Back.Services.Email;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Preparing the no-reply sender against the real PostgreSQL unique index on the mailbox
/// address (recette R1-16): a row for the address that the heartbeat flagged absent from the server
/// must be reused, not inserted again.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MailNoReplySenderIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const string NoReply = "no-reply@example.org";

    [Fact]
    public async Task APreviouslyCreatedMailboxGoneFromTheServer_IsCreatedAgainOnTheSameRow_WithTheStoredPassword()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (serverId, existingId) = await SeedAsync(
            new MailAccount { Email = NoReply, IsActive = false, MissingSince = DateTime.UtcNow.AddDays(-2), QuotaMb = 0 }, ct);
        var stored = new List<CreateSecretRequest>();

        await PrepareAsync(stored, ct);

        await using var db = NewContext();
        var account = Assert.Single(await db.MailAccounts.Where(a => a.Email == NoReply).ToListAsync(ct));
        Assert.Equal(existingId, account.Id);
        Assert.True(account.IsActive);
        Assert.Null(account.MissingSince);
        Assert.Equal(100, account.QuotaMb);
        var secret = Assert.Single(stored);
        Assert.Equal(EmailSenderSettingKeys.NoReplyPasswordSecret(NoReply), secret.Key);
        var task = Assert.Single(await db.Tasks.Where(t => t.ServerId == serverId && t.Operation == OperationKind.MailAddAccount).ToListAsync(ct));
        Assert.Contains(secret.Value, task.EnvironmentVariables ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMailboxDisabledByHandButStillOnTheServer_GetsThePasswordChangedAndIsEnabledAgain()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (serverId, existingId) = await SeedAsync(new MailAccount { Email = NoReply, IsActive = false, QuotaMb = 100 }, ct);
        var stored = new List<CreateSecretRequest>();

        await PrepareAsync(stored, ct);

        await using var db = NewContext();
        var account = Assert.Single(await db.MailAccounts.Where(a => a.Email == NoReply).ToListAsync(ct));
        Assert.Equal(existingId, account.Id);
        Assert.True(account.IsActive);
        Assert.Single(await db.Tasks.Where(t => t.ServerId == serverId && t.Operation == OperationKind.MailChangePassword).ToListAsync(ct));
        Assert.Empty(await db.Tasks.Where(t => t.ServerId == serverId && t.Operation == OperationKind.MailAddAccount).ToListAsync(ct));
        Assert.Single(stored);
    }

    [Fact]
    public async Task CreatingAMailboxThatAlreadyExists_IsAConflict_NotAUniqueIndexFailure()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (serverId, _) = await SeedAsync(new MailAccount { Email = NoReply, IsActive = true, QuotaMb = 100 }, ct);

        await using var db = NewContext();
        var exception = await Assert.ThrowsAsync<ConflictException>(() => NewMailService(db).CreateAccountAsync(
            serverId, new CreateMailAccountRequest { Email = NoReply, Password = "secure-password", QuotaMb = 100 }, ct));

        Assert.Contains(NoReply, exception.Message, StringComparison.Ordinal);
        Assert.Empty(await db.Tasks.Where(t => t.ServerId == serverId && t.Operation == OperationKind.MailAddAccount).ToListAsync(ct));
    }

    private async Task PrepareAsync(List<CreateSecretRequest> stored, CancellationToken ct)
    {
        await using var db = NewContext();
        var resolver = new EmailSenderConfigurationResolver(new EmailSenderSettingsRepository(db), IdentityEncryption());
        var settings = Substitute.For<ISettingsService>();
        await settings.CreateSecretAsync(Arg.Do<CreateSecretRequest>(stored.Add), Arg.Any<CancellationToken>());
        var senderSettings = Substitute.For<IEmailSenderSettingsService>();
        senderSettings.GetAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderSettingsDto());

        await new MailNoReplySenderService(resolver, NewMailService(db), settings, senderSettings).PrepareAsync(ct);
    }

    private static IEncryptionService IdentityEncryption()
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return encryption;
    }

    private static MailService NewMailService(AppDbContext db)
    {
        var serverRepository = Substitute.For<IServerRepository>();
        serverRepository.FindServerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => new Server { Id = call.Arg<int>(), Name = "mail-server", Hostname = "mail-host", MailSetupAvailable = true });
        return new MailService(
            new MailRepository(db), Substitute.For<IAuditService>(), IdentityEncryption(), serverRepository,
            Substitute.For<ITaskService>(), Substitute.For<IMailMasterSecrets>());
    }

    private async Task<(int ServerId, int AccountId)> SeedAsync(MailAccount account, CancellationToken ct)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = "Mail no-reply", Slug = $"mail-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var server = new Server { Name = "mail-server", Hostname = "mail-host", OrganizationId = organization.Id, MailSetupAvailable = true };
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        db.MailStates.Add(new MailState
        {
            ServerId = server.Id,
            Hostname = "mail.example.org",
            IsManagedByAetheus = true,
            HelperVersion = MailService.QuotaMinimumHelperVersion
        });
        var domain = new MailDomain { ServerId = server.Id, Name = "example.org" };
        domain.Accounts.Add(account);
        db.MailDomains.Add(domain);
        await db.SaveChangesAsync(ct);
        return (server.Id, account.Id);
    }
}
