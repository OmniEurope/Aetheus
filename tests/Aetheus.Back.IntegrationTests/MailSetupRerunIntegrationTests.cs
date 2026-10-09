// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R2-101, on the real PostgreSQL schema: a mail setup run again on a domain already recorded
/// answered HTTP 500 (23505 on the unique (ServerId, Name) index) although its task had been queued.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MailSetupRerunIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task ASetupRunTwiceOnOneDomain_KeepsOneDomainAndOneMailbox_AndQueuesBothTasks()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int serverId;
        await using (var seed = NewContext())
        {
            var organization = new Organization { Name = "Mail rerun", Slug = $"mail-{Guid.NewGuid():N}" };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(ct);
            var server = new Server { Name = "mail-server", Hostname = "mail-host", OrganizationId = organization.Id, MailSetupAvailable = true };
            seed.Servers.Add(server);
            await seed.SaveChangesAsync(ct);
            serverId = server.Id;
        }

        var first = await RunSetupAsync(serverId, quotaMb: 1024, ct);
        var second = await RunSetupAsync(serverId, quotaMb: 4096, ct);

        await using var db = NewContext();
        var domain = Assert.Single(await db.MailDomains.Where(d => d.ServerId == serverId).ToListAsync(ct));
        Assert.Equal("example.com", domain.Name);
        var account = Assert.Single(await db.MailAccounts.Where(a => a.MailDomainId == domain.Id).ToListAsync(ct));
        Assert.Equal(4096, account.QuotaMb);
        var tasks = await db.Tasks.Where(t => t.ServerId == serverId && t.Operation == OperationKind.MailSetup)
            .Select(t => t.Id).ToListAsync(ct);
        Assert.Equal([first.TaskId, second.TaskId], tasks.Order());
    }

    private async Task<MailTaskQueuedDto> RunSetupAsync(int serverId, int quotaMb, CancellationToken ct)
    {
        await using var db = NewContext();
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var serverRepository = Substitute.For<IServerRepository>();
        serverRepository.FindServerAsync(serverId, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = serverId, Name = "mail-server", Hostname = "mail-host", MailSetupAvailable = true });
        var service = new MailService(
            new MailRepository(db),
            Substitute.For<IAuditService>(),
            encryption,
            serverRepository,
            Substitute.For<ITaskService>());

        var queued = await service.SetupAsync(serverId, new MailSetupRequest
        {
            Hostname = "mail.example.com",
            Domain = "example.com",
            DkimSelector = "default",
            AdminEmail = "admin@example.com",
            AdminPassword = "secure-password",
            QuotaMb = quotaMb
        }, ct);

        // The first task is still pending when the setup is run again: close it as the agent would, so
        // the R2-085 "a setup is already running" guard does not refuse the re-run.
        await db.Tasks.Where(t => t.Id == queued.TaskId)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.Status, TaskExecutionStatus.Success), ct);
        return queued;
    }
}
