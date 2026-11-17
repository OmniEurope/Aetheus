// SPDX-License-Identifier: EUPL-1.2
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The SQL command counter counts a measure once, even when another counter is alive at the same time:
/// the integration classes run in parallel and several of them hold a counter while they measure.
/// </summary>
public sealed class SqlCommandCounterIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task MeasureAsync_AnotherCounterAlive_CountsEachCommandOnce()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        await using var db = NewContext();

        using var measuring = new SqlCommandCounter();
        using var otherClassCounter = new SqlCommandCounter();
        var measure = await measuring.MeasureAsync(() => db.Organizations.CountAsync(ct));

        Assert.Equal(1, measure.Commands);
    }
}
