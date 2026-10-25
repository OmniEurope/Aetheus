// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Alerts;

public sealed class AlertOccurrenceRetentionServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private readonly ServiceProvider _provider;
    private readonly AlertOccurrenceRetentionService _service;

    public AlertOccurrenceRetentionServiceTests()
    {
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options));
        services.AddScoped<IAlertOccurrenceRepository, AlertOccurrenceRepository>();
        _provider = services.BuildServiceProvider();
        _service = new AlertOccurrenceRetentionService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AlertOccurrenceRetentionService>.Instance,
            new FakeTimeProvider(new DateTimeOffset(Now)));
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task Sweep_DeletesOccurrencesOlderThan365Days_AndKeepsYoungerOnes()
    {
        await SeedAsync(Now.AddDays(-366), Now.AddDays(-400), Now.AddDays(-364), Now);

        var deleted = await _service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, deleted);
        Assert.Equal(
            [Now.AddDays(-364), Now],
            (await RemainingAsync()).OrderBy(date => date).ToList());
    }

    [Fact]
    public async Task Sweep_KeepsAnOccurrenceTriggeredExactlyAtTheCutoff_AndDeletesOneSecondBefore()
    {
        var cutoff = Now.AddDays(-AlertOccurrenceRetentionService.RetentionDays);
        await SeedAsync(cutoff, cutoff.AddSeconds(-1));

        var deleted = await _service.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.Equal([cutoff], await RemainingAsync());
    }

    [Fact]
    public async Task Sweep_WithNothingExpired_DeletesNothing()
    {
        await SeedAsync(Now.AddDays(-10));

        Assert.Equal(0, await _service.SweepAsync(TestContext.Current.CancellationToken));
        Assert.Single(await RemainingAsync());
    }

    private async Task SeedAsync(params DateTime[] triggeredAt)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AlertOccurrences.AddRange(triggeredAt.Select(at => new AlertOccurrence
        {
            RuleName = "rule",
            Metric = "Cpu",
            TriggeredAt = at
        }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<DateTime>> RemainingAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlertOccurrences.Select(o => o.TriggeredAt)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
