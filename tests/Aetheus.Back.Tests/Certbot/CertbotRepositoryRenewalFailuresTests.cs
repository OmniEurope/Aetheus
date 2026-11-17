// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Certbot;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Certbot;

/// <summary>Recette R-19: the failed certificates the "test the failed ones again" action reads come from the state the
/// server recorded for that server only.</summary>
public sealed class CertbotRepositoryRenewalFailuresTests
{
    private static AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero)));

    private static async Task<AppDbContext> DbWithStatesAsync(params (int ServerId, string? Json)[] states)
    {
        var db = CreateDb();
        foreach (var (serverId, json) in states)
            db.CertbotStates.Add(new CertbotState { ServerId = serverId, RenewalCheckFailuresJson = json });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return db;
    }

    [Fact]
    public async Task ReturnsTheDistinctNamesOfThatServerOnly()
    {
        await using var db = await DbWithStatesAsync(
            (1, """[{"Name":"random.example.com","Reason":"x"},{"Name":"demo.example.com","Reason":""},{"Name":"random.example.com","Reason":"again"}]"""),
            (2, """[{"Name":"other.example.com","Reason":"x"}]"""));

        var names = await new CertbotRepository(db).GetRenewalCheckFailedNamesAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(["random.example.com", "demo.example.com"], names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public async Task ReturnsNothingWhenTheRecordedFailuresAreAbsentOrUnreadable(string? json)
    {
        await using var db = await DbWithStatesAsync((1, json));

        Assert.Empty(await new CertbotRepository(db).GetRenewalCheckFailedNamesAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReturnsNothingForAServerWithoutRecordedState()
    {
        await using var db = CreateDb();

        Assert.Empty(await new CertbotRepository(db).GetRenewalCheckFailedNamesAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheHelperVersionIsThatOfTheServerRecordedState_AndZeroWithoutOne()
    {
        await using var db = CreateDb();
        db.CertbotStates.Add(new CertbotState { ServerId = 1, HelperVersion = 9 });
        db.CertbotStates.Add(new CertbotState { ServerId = 2, HelperVersion = 10 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new CertbotRepository(db);

        Assert.Equal(9, await repository.GetHelperVersionAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(10, await repository.GetHelperVersionAsync(2, TestContext.Current.CancellationToken));
        Assert.Equal(0, await repository.GetHelperVersionAsync(3, TestContext.Current.CancellationToken));
    }
}
