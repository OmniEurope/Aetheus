// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-002 lot 10, gap 4 (recette R1-41, commit 9b724525a): an administrator opens a mailbox without
/// its password through <c>POST /api/servers/{id}/mail/accounts/{id}/mailbox/open-as-administrator</c>.
/// Only the IMAP wire (<see cref="IMailboxConnector"/>, the boundary the mailbox unit tests substitute)
/// is replaced; the HTTP pipeline, the role check, the master secret (stored encrypted through the real
/// settings service and read back from PostgreSQL) and the audit chain are real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MailboxAdministratorOpenHttpIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AdministratorOpensTheMailbox_AsTheMasterUser_AndTheOpeningIsAudited()
    {
        var ct = TestContext.Current.CancellationToken;
        var wire = new RecordingConnector();
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithConnector(baseFactory, wire);
        var mailbox = await SeedMailboxAsync(factory);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var auditsBefore = await OpeningAuditsAsync(factory, mailbox.ServerId);

        var response = await admin.PostAsync(OpenUrl(mailbox), null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var access = await response.Content.ReadFromJsonAsync<MailboxAccessDto>(IntegrationJsonOptions.Default, ct);
        Assert.True(access!.IsUnlocked);
        Assert.True(access.OpenedAsAdministrator);
        Assert.Equal(mailbox.Email, access.Email);
        Assert.DoesNotContain(mailbox.MasterPassword, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // The IMAP login used the master user read back from the encrypted store, never a typed password.
        var endpoint = Assert.Single(wire.Opened);
        Assert.Equal(MailMasterAccess.Login(mailbox.Email), endpoint.Username);
        Assert.Equal(mailbox.MasterPassword, endpoint.Password);

        var audits = await OpeningAuditsAsync(factory, mailbox.ServerId);
        var entry = Assert.Single(audits, a => auditsBefore.All(before => before.Id != a.Id));
        Assert.Equal("admin", entry.Username);
        Assert.Equal(mailbox.Email, entry.Details);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.AuditLogs.AsNoTracking()
                .CountAsync(a => a.Details != null && a.Details.Contains(mailbox.MasterPassword), ct));
            var stored = await db.Secrets.AsNoTracking()
                .SingleAsync(s => s.Key == MailMasterAccess.SecretKey(mailbox.ServerId), ct);
            Assert.DoesNotContain(mailbox.MasterPassword, stored.EncryptedValue, StringComparison.Ordinal);
        }

        // The held master credential now serves the session: the access reads as opened by an administrator.
        var state = await admin.GetFromJsonAsync<MailboxAccessDto>(AccessUrl(mailbox), IntegrationJsonOptions.Default, ct);
        Assert.True(state!.OpenedAsAdministrator);
        Assert.True(state.CanOpenAsAdministrator);
    }

    [Fact]
    public async Task NonAdministratorWithFullServerRights_IsRefused_NothingIsOpenedNorAudited()
    {
        var ct = TestContext.Current.CancellationToken;
        var wire = new RecordingConnector();
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithConnector(baseFactory, wire);
        var mailbox = await SeedMailboxAsync(factory);
        var operatorUser = await IntegrationUsers.CreateAsync(factory.Services, "mail-operator",
            [new IntegrationUsers.Grant(ResourceType.Server, mailbox.ServerId, Permission.Admin)]);
        using var client = await IntegrationUsers.LoginClientAsync(factory, operatorUser.Username);
        var auditsBefore = await OpeningAuditsAsync(factory, mailbox.ServerId);

        var response = await client.PostAsync(OpenUrl(mailbox), null, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(wire.Opened);
        Assert.Equal(auditsBefore.Count, (await OpeningAuditsAsync(factory, mailbox.ServerId)).Count);

        // The operator still reads the mailbox state (Server rights are real), is not offered the master
        // opening, and the mailbox stays locked.
        var state = await client.GetFromJsonAsync<MailboxAccessDto>(AccessUrl(mailbox), IntegrationJsonOptions.Default, ct);
        Assert.False(state!.CanOpenAsAdministrator);
        Assert.False(state.IsUnlocked);
    }

    private sealed record SeededMailbox(int ServerId, int AccountId, string Email, string MasterPassword);

    private static string OpenUrl(SeededMailbox m) =>
        $"/api/servers/{m.ServerId}/mail/accounts/{m.AccountId}/mailbox/open-as-administrator";

    private static string AccessUrl(SeededMailbox m) =>
        $"/api/servers/{m.ServerId}/mail/accounts/{m.AccountId}/mailbox";

    private static WebApplicationFactory<Program> WithConnector(AetheusWebApplicationFactory factory, IMailboxConnector connector) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(connector)));

    /// <summary>A server with one active mailbox and a prepared administrator access (master password).</summary>
    private static async Task<SeededMailbox> SeedMailboxAsync(WebApplicationFactory<Program> factory)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await db.Organizations.Where(o => o.Slug == "aetheus").Select(o => o.Id).FirstAsync(ct);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var server = new Server
        {
            Name = $"mail-{suffix}",
            Hostname = $"mail-{suffix}.test",
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        var domain = new MailDomain { ServerId = server.Id, Name = $"{suffix}.test", CreatedAt = DateTime.UtcNow };
        db.MailDomains.Add(domain);
        await db.SaveChangesAsync(ct);
        var account = new MailAccount { MailDomainId = domain.Id, Email = $"boss@{suffix}.test", CreatedAt = DateTime.UtcNow };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync(ct);

        var masterPassword = MailMasterAccess.NewPassword();
        await scope.ServiceProvider.GetRequiredService<IMailMasterSecrets>().StoreAsync(server.Id, masterPassword, ct);
        return new SeededMailbox(server.Id, account.Id, account.Email, masterPassword);
    }

    private static async Task<List<AuditLog>> OpeningAuditsAsync(WebApplicationFactory<Program> factory, int serverId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "MailboxAdministratorOpened" && a.EntityType == "Mail" && a.EntityId == serverId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The IMAP/SMTP wire: accepts every login and records the endpoint it was asked to open.</summary>
    private sealed class RecordingConnector : IMailboxConnector
    {
        public ConcurrentQueue<MailboxEndpoint> Opened { get; } = new();

        public Task<IMailboxSession> OpenAsync(MailboxEndpoint endpoint, CancellationToken ct)
        {
            Opened.Enqueue(endpoint);
            return Task.FromResult(Substitute.For<IMailboxSession>());
        }

        public Task SendAsync(MailboxEndpoint endpoint, MimeKit.MimeMessage message, CancellationToken ct) =>
            throw new NotSupportedException("No message is sent by the administrator opening.");
    }
}
