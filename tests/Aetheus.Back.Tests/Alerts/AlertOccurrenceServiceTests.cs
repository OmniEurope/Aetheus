// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary>Recette R-52: every alert that fires is stored with an identifier of its own, which the
/// notification mail's button and the alert page use.</summary>
public sealed class AlertOccurrenceServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);
    private readonly AppDbContext _db;
    private readonly AlertOccurrenceService _sut;

    public AlertOccurrenceServiceTests()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(new DateTimeOffset(Now)));
        _sut = new AlertOccurrenceService(new AlertOccurrenceRepository(_db), Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertOccurrenceService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task RecordAsync_StoresTheAlert_AndReturnsAnIdentifierPerAlert()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _sut.RecordAsync(Occurrence(), ct);
        var second = await _sut.RecordAsync(Occurrence(), ct);

        Assert.NotEqual(first, second);
        var stored = await _db.AlertOccurrences.SingleAsync(item => item.Id == first, ct);
        Assert.Equal(11, stored.ServerId);
        Assert.Equal("web-1", stored.ServerName);
        Assert.Equal("Cpu", stored.Metric);
        Assert.Equal(ComparisonOperator.GreaterThan, stored.Operator);
        Assert.Equal(80, stored.Threshold);
        Assert.Equal(95, stored.ObservedValue);
        Assert.Equal(AlertSeverity.Critical, stored.Severity);
        Assert.Equal(Now, stored.TriggeredAt);
    }

    [Fact]
    public async Task RecordAsync_KeepsTextsWithinTheirColumns()
    {
        var ct = TestContext.Current.CancellationToken;

        var id = await _sut.RecordAsync(Occurrence() with { Message = new string('m', 5_000), ServerName = new string('s', 300) }, ct);

        var stored = await _db.AlertOccurrences.SingleAsync(item => item.Id == id, ct);
        Assert.Equal(4_000, stored.Message!.Length);
        Assert.Equal(200, stored.ServerName!.Length);
    }

    [Fact]
    public async Task GetAsync_ReturnsTheAlert_WithItsRuleState()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.AlertRules.Add(new AlertRule { Id = 5, Name = "CPU high", IsEnabled = false });
        await _db.SaveChangesAsync(ct);
        var id = await _sut.RecordAsync(Occurrence(), ct);

        var alert = await _sut.GetAsync(Assert.NotNull(id), ct);

        Assert.NotNull(alert);
        Assert.Equal(id, alert.Id);
        Assert.Equal("CPU high", alert.RuleName);
        Assert.Equal(false, alert.RuleIsEnabled);
        Assert.Equal(95, alert.ObservedValue);
        Assert.Equal(Now, alert.TriggeredAt);
    }

    [Fact]
    public async Task GetAsync_OfAnAlertWhoseRuleIsGone_HasNoRuleState()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await _sut.RecordAsync(Occurrence() with { AlertRuleId = null }, ct);

        var alert = await _sut.GetAsync(Assert.NotNull(id), ct);

        Assert.NotNull(alert);
        Assert.Null(alert.RuleIsEnabled);
    }

    [Fact]
    public async Task GetAsync_Unknown_ReturnsNull()
    {
        Assert.Null(await _sut.GetAsync(404, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordHostAlertAsync_StoresAHostCheckWithoutARule_AndReadsTheSeverityWord()
    {
        var ct = TestContext.Current.CancellationToken;

        var id = await _sut.RecordHostAlertAsync("OOM", 9, "vps-1", "OomKill", "critical", "OOM kill", Now, ct);
        var unknown = await _sut.RecordHostAlertAsync("Sec-Audit", 9, "vps-1", "SshConfigChange", "mystery", null, Now, ct);

        var stored = await _db.AlertOccurrences.SingleAsync(item => item.Id == id, ct);
        Assert.Null(stored.AlertRuleId);
        Assert.Null(stored.Operator);
        Assert.Null(stored.Threshold);
        Assert.Equal(AlertSeverity.Critical, stored.Severity);
        Assert.Equal(AlertSeverity.Warning, (await _db.AlertOccurrences.SingleAsync(item => item.Id == unknown, ct)).Severity);
    }

    /// <summary>Losing an alert is worse than losing its page: a row the database refuses gives no identifier, and
    /// the caller still emits the alert.</summary>
    [Fact]
    public async Task RecordAsync_WhenTheDatabaseRefusesTheRow_ReturnsNoIdentifier_InsteadOfThrowing()
    {
        var repository = NSubstitute.Substitute.For<IAlertOccurrenceRepository>();
        NSubstitute.ExceptionExtensions.ExceptionExtensions
            .ThrowsAsync(repository.AddAsync(NSubstitute.Arg.Any<AlertOccurrence>(), NSubstitute.Arg.Any<CancellationToken>()),
                new DbUpdateException("insert refused"));
        var sut = new AlertOccurrenceService(repository, Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertOccurrenceService>.Instance);

        Assert.Null(await sut.RecordAsync(Occurrence(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordAsync_WhenTheCallIsCancelled_StillThrows()
    {
        var repository = NSubstitute.Substitute.For<IAlertOccurrenceRepository>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        NSubstitute.ExceptionExtensions.ExceptionExtensions
            .ThrowsAsync(repository.AddAsync(NSubstitute.Arg.Any<AlertOccurrence>(), NSubstitute.Arg.Any<CancellationToken>()),
                new InvalidOperationException("context disposed by the shutdown"));
        var sut = new AlertOccurrenceService(repository, Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertOccurrenceService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RecordAsync(Occurrence(), cancelled.Token));
    }

    /// <summary>A row that failed must not stay pending in the shared context, or the next write of the same scope
    /// (the next rule of the evaluation pass) would retry it and fail too.</summary>
    [Fact]
    public async Task TheRepository_DetachesARowItCouldNotStore_SoTheNextWriteOfTheScopeSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new AlertOccurrenceRepository(_db);
        await repository.AddAsync(Entity(id: 1), ct);
        _db.ChangeTracker.Clear();

        var duplicate = Entity(id: 1);
        await Assert.ThrowsAnyAsync<Exception>(() => repository.AddAsync(duplicate, ct));

        Assert.Equal(EntityState.Detached, _db.Entry(duplicate).State);
        await repository.AddAsync(Entity(id: 2), ct);
        Assert.Equal(2, await _db.AlertOccurrences.CountAsync(ct));
    }

    private static AlertOccurrence Entity(long id) => new()
    {
        Id = id,
        RuleName = "CPU high",
        Metric = "Cpu",
        Severity = AlertSeverity.Warning,
        TriggeredAt = Now
    };

    private static NewAlertOccurrence Occurrence() => new()
    {
        AlertRuleId = 5,
        RuleName = "CPU high",
        ServerId = 11,
        ServerName = "web-1",
        Metric = "Cpu",
        Operator = ComparisonOperator.GreaterThan,
        Threshold = 80,
        ObservedValue = 95,
        SustainedSeconds = 60,
        Severity = AlertSeverity.Critical,
        TriggeredAt = Now
    };
}
