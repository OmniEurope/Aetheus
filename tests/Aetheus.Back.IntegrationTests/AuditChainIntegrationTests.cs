// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// SITE-SEC-003 on real PostgreSQL: a timestamp carrying ticks below the microsecond is written, read
/// back by a fresh context, and the chain still verifies. The InMemory suite kept the ticks and could
/// not see that PostgreSQL drops them.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuditChainIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task EntriesWrittenWithSubMicrosecondTicks_VerifyAfterReadBack()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var clock = new SteppedClock(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero).AddTicks(1_234_567));

        await using (var write = NewContext())
        {
            var repo = new AuditRepository(write);
            var audit = new AuditService(repo, new HttpContextAccessor(), new AuditChainService(repo),
                new MemoryCache(new MemoryCacheOptions()), clock);
            await audit.LogAsync("Created", "Server", 7, "first", ct);
            clock.Now = clock.Now.AddTicks(3_333);
            await audit.LogAsync("Deleted", "Server", 7, "second", ct);
        }

        await using var read = NewContext();
        var result = await new AuditChainService(new AuditRepository(read)).VerifyChainAsync(ct);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal(2, result.TotalEntries);
    }

    private sealed class SteppedClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
