// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

public sealed class ServerEnrollmentTransactionIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task ConcurrentEnrollmentWithSameRegistrationToken_PersistsExactlyOneServerAndBearer()
    {
        await ResetAndMigrateAsync();
        int registrationTokenId;

        await using (var seed = NewContext())
        {
            var organization = new Organization
            {
                Name = "Enrollment transaction",
                Slug = $"enrollment-{Guid.NewGuid():N}"
            };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var registrationToken = new RegistrationToken
            {
                Token = new string('a', 64),
                OrganizationId = organization.Id,
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            };
            seed.RegistrationTokens.Add(registrationToken);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            registrationTokenId = registrationToken.Id;
        }

        await using var dbA = new AppDbContext(BuildOptions());
        await using var dbB = new AppDbContext(BuildOptions());
        var repositoryA = new AuthRepository(dbA, TimeProvider.System);
        var repositoryB = new AuthRepository(dbB, TimeProvider.System);
        var serverA = new Server
        {
            Name = "enrollment-a",
            Hostname = "enrollment-a",
            OrganizationId = await dbA.RegistrationTokens
                .Where(token => token.Id == registrationTokenId)
                .Select(token => token.OrganizationId)
                .SingleAsync(TestContext.Current.CancellationToken)
        };
        var serverB = new Server
        {
            Name = "enrollment-b",
            Hostname = "enrollment-b",
            OrganizationId = serverA.OrganizationId
        };

        var outcomes = await Task.WhenAll(
            repositoryA.TryPersistServerEnrollmentAsync(
                registrationTokenId,
                serverA,
                new ServerToken
                {
                    Server = serverA,
                    TokenHash = new string('b', 64),
                    ExpiresAt = DateTime.UtcNow.AddDays(1)
                },
                TestContext.Current.CancellationToken),
            repositoryB.TryPersistServerEnrollmentAsync(
                registrationTokenId,
                serverB,
                new ServerToken
                {
                    Server = serverB,
                    TokenHash = new string('c', 64),
                    ExpiresAt = DateTime.UtcNow.AddDays(1)
                },
                TestContext.Current.CancellationToken));

        Assert.Single(outcomes, outcome => outcome);
        Assert.Single(outcomes, outcome => !outcome);

        await using var verify = NewContext();
        var persistedServer = Assert.Single(await verify.Servers.ToListAsync(TestContext.Current.CancellationToken));
        var bearer = Assert.Single(await verify.ServerTokens.ToListAsync(TestContext.Current.CancellationToken));
        var consumed = await verify.RegistrationTokens
            .SingleAsync(token => token.Id == registrationTokenId, TestContext.Current.CancellationToken);
        Assert.True(consumed.IsUsed);
        Assert.Equal(persistedServer.Id, consumed.UsedByServerId);
        Assert.Equal(persistedServer.Id, bearer.ServerId);
    }
}
