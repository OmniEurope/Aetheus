// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Concurrent HTTP mail-setup requests on fresh PostgreSQL-backed servers must never
/// return 500 and must queue exactly one setup per server.</summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class MailSetupConcurrencyIntegrationTests(ApiSmokeFixture fixture)
{
    private const int Rounds = 6;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoSimultaneousSetupsOfANewDomain_AnswerOkAndConflict_AndQueueOneTask()
    {
        using var client = fixture.CreateAdminClient();
        for (var round = 0; round < Rounds; round++)
        {
            var serverId = await SeedMailServerAsync();
            var domain = $"r{round}-{Guid.NewGuid():N}.example.com";

            var answers = await Task.WhenAll(
                client.PostAsJsonAsync($"/api/servers/{serverId}/mail/setup", Setup(domain), Ct),
                client.PostAsJsonAsync($"/api/servers/{serverId}/mail/setup", Setup(domain), Ct));

            var statuses = answers.Select(answer => answer.StatusCode).Order().ToArray();
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.Tasks.AsNoTracking()
                .Where(task => task.ServerId == serverId && task.Operation == OperationKind.MailSetup).ToListAsync(Ct));
            var recorded = Assert.Single(await db.MailDomains.AsNoTracking()
                .Where(row => row.ServerId == serverId).ToListAsync(Ct));
            Assert.Single(await db.MailAccounts.AsNoTracking()
                .Where(account => account.MailDomainId == recorded.Id).ToListAsync(Ct));
        }
    }

    private static MailSetupRequest Setup(string domain) => new()
    {
        Hostname = $"mail.{domain}",
        Domain = domain,
        DkimSelector = "default",
        AdminEmail = $"admin@{domain}",
        AdminPassword = "secure-password",
        QuotaMb = 1024
    };

    private async Task<int> SeedMailServerAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = await db.Organizations.FirstAsync(o => o.Slug == "aetheus", Ct);
        var server = new Server
        {
            Name = $"mail-race-{Guid.NewGuid():N}",
            Hostname = $"mail-race-{Guid.NewGuid():N}.test",
            OrganizationId = organization.Id,
            Status = ServerStatus.Online,
            MailSetupAvailable = true
        };
        db.Servers.Add(server);
        await db.SaveChangesAsync(Ct);
        return server.Id;
    }
}
