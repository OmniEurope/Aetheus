// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>The destructive reset endpoint must reject databases without the _e2e suffix. The
/// disposable integration database verifies refusal without dropping its contents.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DevControllerResetDbGuardIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ResetDb_OnNonE2eDatabase_IsRefused_AndDropsNothing()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, TestContext.Current.CancellationToken));

        var resp = await client.PostAsync("/api/dev/reset-db", null, cancellationToken: TestContext.Current.CancellationToken);

        // The container DB is "aetheus_it" (not *_e2e), so the destructive endpoint must refuse.
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // And it dropped nothing: the seeded admin user (and the schema) are still intact.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Users.AnyAsync(u => u.Username == "admin", cancellationToken: TestContext.Current.CancellationToken));
    }
}
