// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The four set-based purges (<c>ExecuteDeleteAsync</c> branch taken on a relational provider only) run
/// against real PostgreSQL: the unit suite uses EF InMemory, which only exercises their load-and-remove
/// fallback. Each purge deletes the rows strictly older than the cut-off, so a row exactly at the cut-off and
/// every newer row stay; none of the four keeps a per-entity cap or keep-N rule, and each returns the number
/// of rows it deleted. A second call with the same cut-off finds nothing and returns zero.
/// </summary>
public sealed class SetBasedPurgeIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static readonly DateTime Cutoff = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private async Task<Organization> SeedOrganizationAsync(AppDbContext db, CancellationToken ct)
    {
        var organization = new Organization { Name = $"purge-{Guid.NewGuid():N}", Slug = $"purge-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        return organization;
    }

    private async Task<Server> SeedServerAsync(AppDbContext db, string name, CancellationToken ct)
    {
        var organization = await SeedOrganizationAsync(db, ct);
        var server = new Server { Name = name, Hostname = $"{name}.test", OrganizationId = organization.Id };
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        return server;
    }

    [Fact]
    public async Task TaskLogPurge_DeletesTheLinesOlderThanTheCutoff_AcrossTasks_AndKeepsTheOthers()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int expiredA, expiredB, atCutoff, justAfter, recentOtherTask, oldOtherTask;
        await using (var db = NewContext())
        {
            var server = await SeedServerAsync(db, "purge-logs", ct);
            var first = new ServerTask { ServerId = server.Id, Name = "first", Command = "echo" };
            var second = new ServerTask { ServerId = server.Id, Name = "second", Command = "echo" };
            db.Tasks.AddRange(first, second);
            await db.SaveChangesAsync(ct);

            var wanted = new[]
            {
                Cutoff.AddDays(-30), Cutoff.AddTicks(-1), Cutoff, Cutoff.AddTicks(1), Cutoff.AddDays(5), Cutoff.AddDays(-1)
            };
            var lines = new[]
            {
                NewLine(first.Id, wanted[0]),
                NewLine(first.Id, wanted[1]),
                NewLine(first.Id, wanted[2]),
                NewLine(first.Id, wanted[3]),
                NewLine(second.Id, wanted[4]),
                NewLine(second.Id, wanted[5])
            };
            db.TaskLogs.AddRange(lines);
            await db.SaveChangesAsync(ct);
            // AppDbContext stamps TaskLog.Timestamp with the clock on insert, so the wanted instants are
            // written afterwards by a set-based update (which bypasses that stamping).
            for (var index = 0; index < lines.Length; index++)
            {
                var (id, timestamp) = (lines[index].Id, wanted[index]);
                await db.TaskLogs.Where(l => l.Id == id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.Timestamp, timestamp), ct);
            }

            (expiredA, expiredB, atCutoff, justAfter, recentOtherTask, oldOtherTask) =
                (lines[0].Id, lines[1].Id, lines[2].Id, lines[3].Id, lines[4].Id, lines[5].Id);
        }

        await using (var db = NewContext())
        {
            var repository = new LogRepository(db);
            Assert.Equal(3, await repository.DeleteLogsOlderThanAsync(Cutoff, ct));
            Assert.Equal(0, await repository.DeleteLogsOlderThanAsync(Cutoff, ct));
        }

        await using var check = NewContext();
        var remaining = await check.TaskLogs.AsNoTracking().Select(l => l.Id).OrderBy(id => id).ToListAsync(ct);
        Assert.Equal(new[] { atCutoff, justAfter, recentOtherTask }.Order(), remaining);
        Assert.DoesNotContain(expiredA, remaining);
        Assert.DoesNotContain(expiredB, remaining);
        Assert.DoesNotContain(oldOtherTask, remaining);
        Assert.Equal(2, await check.Tasks.CountAsync(ct));
    }

    private static TaskLog NewLine(int taskId, DateTime timestamp) =>
        new() { TaskId = taskId, Level = TaskLogLevel.Info, Message = "line", Timestamp = timestamp };

    [Fact]
    public async Task ServerMetricPurge_DeletesTheSamplesOlderThanTheCutoff_ForEveryServer_AndKeepsTheOthers()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int expiredA, expiredB, expiredOtherServer, atCutoff, recent, recentOtherServer;
        await using (var db = NewContext())
        {
            var serverA = await SeedServerAsync(db, "purge-metrics-a", ct);
            var serverB = await SeedServerAsync(db, "purge-metrics-b", ct);
            var metrics = new[]
            {
                new ServerMetric { ServerId = serverA.Id, Timestamp = Cutoff.AddDays(-60) },
                new ServerMetric { ServerId = serverA.Id, Timestamp = Cutoff.AddTicks(-1) },
                new ServerMetric { ServerId = serverB.Id, Timestamp = Cutoff.AddHours(-1) },
                new ServerMetric { ServerId = serverA.Id, Timestamp = Cutoff },
                new ServerMetric { ServerId = serverA.Id, Timestamp = Cutoff.AddMinutes(1) },
                new ServerMetric { ServerId = serverB.Id, Timestamp = Cutoff.AddDays(3) }
            };
            db.ServerMetrics.AddRange(metrics);
            await db.SaveChangesAsync(ct);
            (expiredA, expiredB, expiredOtherServer, atCutoff, recent, recentOtherServer) =
                (metrics[0].Id, metrics[1].Id, metrics[2].Id, metrics[3].Id, metrics[4].Id, metrics[5].Id);
        }

        await using (var db = NewContext())
        {
            var repository = new ServerHeartbeatRepository(db);
            Assert.Equal(3, await repository.DeleteMetricsOlderThanAsync(Cutoff, ct));
            Assert.Equal(0, await repository.DeleteMetricsOlderThanAsync(Cutoff, ct));
        }

        await using var check = NewContext();
        var remaining = await check.ServerMetrics.AsNoTracking().Select(m => m.Id).OrderBy(id => id).ToListAsync(ct);
        Assert.Equal(new[] { atCutoff, recent, recentOtherServer }.Order(), remaining);
        Assert.DoesNotContain(expiredA, remaining);
        Assert.DoesNotContain(expiredB, remaining);
        Assert.DoesNotContain(expiredOtherServer, remaining);
        Assert.Equal(2, await check.Servers.CountAsync(server => server.Name.StartsWith("purge-metrics-"), ct));
    }

    [Fact]
    public async Task AlertOccurrencePurge_DeletesTheOccurrencesTriggeredBeforeTheCutoff_AndKeepsTheOthers()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        long expiredRule, expiredHostSecurity, atCutoff, recent;
        int ruleId;
        await using (var db = NewContext())
        {
            var server = await SeedServerAsync(db, "purge-alerts", ct);
            var rule = new AlertRule
            {
                Name = "cpu high",
                ServerId = server.Id,
                Metric = MetricType.Cpu,
                Operator = ComparisonOperator.GreaterThan,
                Threshold = 90,
                CreatedAt = Cutoff,
                UpdatedAt = Cutoff
            };
            db.AlertRules.Add(rule);
            await db.SaveChangesAsync(ct);
            ruleId = rule.Id;

            var occurrences = new[]
            {
                NewOccurrence(rule.Id, server.Id, Cutoff.AddDays(-90)),
                NewOccurrence(null, server.Id, Cutoff.AddTicks(-1)),
                NewOccurrence(rule.Id, server.Id, Cutoff),
                NewOccurrence(rule.Id, null, Cutoff.AddDays(2))
            };
            db.AlertOccurrences.AddRange(occurrences);
            await db.SaveChangesAsync(ct);
            (expiredRule, expiredHostSecurity, atCutoff, recent) =
                (occurrences[0].Id, occurrences[1].Id, occurrences[2].Id, occurrences[3].Id);
        }

        await using (var db = NewContext())
        {
            var repository = new AlertOccurrenceRepository(db);
            Assert.Equal(2, await repository.DeleteTriggeredBeforeAsync(Cutoff, ct));
            Assert.Equal(0, await repository.DeleteTriggeredBeforeAsync(Cutoff, ct));
        }

        await using var check = NewContext();
        var remaining = await check.AlertOccurrences.AsNoTracking().Select(o => o.Id).OrderBy(id => id).ToListAsync(ct);
        Assert.Equal(new[] { atCutoff, recent }.Order(), remaining);
        Assert.DoesNotContain(expiredRule, remaining);
        Assert.DoesNotContain(expiredHostSecurity, remaining);
        Assert.True(await check.AlertRules.AnyAsync(rule => rule.Id == ruleId, ct));
    }

    private static AlertOccurrence NewOccurrence(int? ruleId, int? serverId, DateTime triggeredAt) =>
        new()
        {
            AlertRuleId = ruleId,
            RuleName = "cpu high",
            ServerId = serverId,
            ServerName = serverId is null ? null : "purge-alerts",
            Metric = "CpuPercent",
            Severity = AlertSeverity.Warning,
            TriggeredAt = triggeredAt
        };

    [Fact]
    public async Task AppErrorPurge_DeletesTheErrorsLastSeenBeforeTheCutoff_AndKeepsTheOthers()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int expiredA, expiredB, oldFirstSeenButRecentlySeen, atCutoff, recent, appId, otherAppId;
        await using (var db = NewContext())
        {
            var organization = await SeedOrganizationAsync(db, ct);
            var project = new Project { Name = $"purge-{Guid.NewGuid():N}", OrganizationId = organization.Id };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            var app = new MonitoredApp { ProjectId = project.Id, Name = "purge-app" };
            var otherApp = new MonitoredApp { ProjectId = project.Id, Name = "purge-other-app" };
            db.MonitoredApps.AddRange(app, otherApp);
            await db.SaveChangesAsync(ct);
            (appId, otherAppId) = (app.Id, otherApp.Id);

            var errors = new[]
            {
                NewError(app.Id, "fp-expired-a", Cutoff.AddDays(-40), Cutoff.AddDays(-30)),
                NewError(otherApp.Id, "fp-expired-b", Cutoff.AddDays(-9), Cutoff.AddTicks(-1)),
                NewError(app.Id, "fp-grouped", Cutoff.AddDays(-100), Cutoff.AddDays(1)),
                NewError(app.Id, "fp-at-cutoff", Cutoff.AddDays(-2), Cutoff),
                NewError(otherApp.Id, "fp-recent", Cutoff, Cutoff.AddDays(7))
            };
            db.AppErrorEvents.AddRange(errors);
            await db.SaveChangesAsync(ct);
            (expiredA, expiredB, oldFirstSeenButRecentlySeen, atCutoff, recent) =
                (errors[0].Id, errors[1].Id, errors[2].Id, errors[3].Id, errors[4].Id);
        }

        await using (var db = NewContext())
        {
            var repository = new AppErrorRepository(db);
            Assert.Equal(2, await repository.PurgeOlderThanAsync(Cutoff, ct));
            Assert.Equal(0, await repository.PurgeOlderThanAsync(Cutoff, ct));
        }

        await using var check = NewContext();
        var remaining = await check.AppErrorEvents.AsNoTracking().Select(e => e.Id).OrderBy(id => id).ToListAsync(ct);
        Assert.Equal(new[] { oldFirstSeenButRecentlySeen, atCutoff, recent }.Order(), remaining);
        Assert.DoesNotContain(expiredA, remaining);
        Assert.DoesNotContain(expiredB, remaining);
        Assert.Equal(2, await check.MonitoredApps.CountAsync(app => app.Id == appId || app.Id == otherAppId, ct));
    }

    private static AppErrorEvent NewError(int appId, string fingerprint, DateTime firstSeenAt, DateTime lastSeenAt) =>
        new()
        {
            MonitoredAppId = appId,
            Fingerprint = fingerprint,
            ExceptionType = "System.InvalidOperationException",
            Message = "boom",
            OccurrenceCount = 1,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = lastSeenAt
        };
}
