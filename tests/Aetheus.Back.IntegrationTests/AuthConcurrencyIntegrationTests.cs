// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// BACK-010 and BACK-011 on real PostgreSQL: concurrent failed logins and concurrent recovery-code
/// consumes, each request on its own context as in production. The InMemory suite cannot show
/// either race, since it has no row lock and no conditional UPDATE.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthConcurrencyIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task ConcurrentFailedLogins_EveryIncrementIsKept_AndTheLastSeesTheThreshold()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var userId = await SeedUserAsync(null, ct);

        var contexts = Enumerable.Range(0, 5).Select(_ => NewContext()).ToList();
        try
        {
            var counts = await Task.WhenAll(contexts.Select(db =>
                new AuthRepository(db, TimeProvider.System).IncrementFailedLoginAsync(userId, ct)));

            Assert.Equal(5, counts.Max());
        }
        finally
        {
            foreach (var db in contexts) await db.DisposeAsync();
        }

        await using var verify = NewContext();
        Assert.Equal(5, await verify.Users.Where(u => u.Id == userId).Select(u => u.FailedLoginCount).SingleAsync(ct));
    }

    [Fact]
    public async Task ConditionalReplace_OfTheSameReadList_SucceedsExactlyOnce()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var read = JsonSerializer.Serialize(new List<string> { "a", "b" });
        var userId = await SeedUserAsync(read, ct);

        await using var first = NewContext();
        await using var second = NewContext();
        var results = await Task.WhenAll(
            new AuthRepository(first, TimeProvider.System).ReplaceTotpRecoveryCodesAsync(userId, read, "[\"b\"]", ct),
            new AuthRepository(second, TimeProvider.System).ReplaceTotpRecoveryCodesAsync(userId, read, "[\"b\"]", ct));

        Assert.Single(results, replaced => replaced);
    }

    [Fact]
    public async Task ConcurrentConsumesOfOneRecoveryCode_OnlyOneLogsIn()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var codes = Codes("AAAAA11111", "BBBBB22222");
        var userId = await SeedUserAsync(codes, ct);

        await using var first = NewContext();
        await using var second = NewContext();
        var results = await Task.WhenAll(
            NewTotpService(first).ConsumeRecoveryCodeAsync(userId, "AAAAA-11111", ct),
            NewTotpService(second).ConsumeRecoveryCodeAsync(userId, "AAAAA-11111", ct));

        Assert.Single(results, accepted => accepted);
        Assert.Single(await StoredCodesAsync(userId, ct));
    }

    [Fact]
    public async Task ConcurrentConsumesOfTwoDifferentCodes_BothAreRemoved()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var userId = await SeedUserAsync(Codes("AAAAA11111", "BBBBB22222", "CCCCC33333"), ct);

        await using var first = NewContext();
        await using var second = NewContext();
        var results = await Task.WhenAll(
            NewTotpService(first).ConsumeRecoveryCodeAsync(userId, "AAAAA-11111", ct),
            NewTotpService(second).ConsumeRecoveryCodeAsync(userId, "BBBBB-22222", ct));

        Assert.All(results, Assert.True);
        // Neither write resurrected the code the other one consumed.
        var remaining = await StoredCodesAsync(userId, ct);
        var only = Assert.Single(remaining);
        Assert.True(BCrypt.Net.BCrypt.Verify("CCCCC33333", only));
    }

    private static string Codes(params string[] plain) =>
        JsonSerializer.Serialize(plain.Select(code => BCrypt.Net.BCrypt.HashPassword(code, workFactor: 4)).ToList());

    private static TotpService NewTotpService(Aetheus.Back.Data.AppDbContext db) => new(
        new AuthRepository(db, TimeProvider.System),
        Substitute.For<IEncryptionService>(),
        Substitute.For<IAuditService>(),
        new JwtOptions { SigningKey = "aetheus-integration-key-minimum-32-bytes" },
        TimeProvider.System);

    private async Task<List<string>> StoredCodesAsync(int userId, CancellationToken ct)
    {
        await using var verify = NewContext();
        var json = await verify.Users.Where(u => u.Id == userId).Select(u => u.TotpRecoveryCodes).SingleAsync(ct);
        return JsonSerializer.Deserialize<List<string>>(json!)!;
    }

    private async Task<int> SeedUserAsync(string? recoveryCodes, CancellationToken ct)
    {
        await using var seed = NewContext();
        var user = new User
        {
            Username = $"race-{Guid.NewGuid():N}",
            PasswordHash = "not-used",
            TotpRecoveryCodes = recoveryCodes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync(ct);
        return user.Id;
    }
}
