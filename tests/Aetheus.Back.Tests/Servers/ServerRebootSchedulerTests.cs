// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>
/// Recette R-8 (option B of 2026-11-07): the reboot scheduler and the backend side of the agent's confirmation, over the
/// real repository and an in-memory database (the decisions are exercised, not the SQL), with a fake clock.
/// </summary>
public sealed class ServerRebootSchedulerTests : IDisposable
{
    // Saturday 2026-11-07, 01:00 UTC; Paris is UTC+1 in November, so 02:05 Paris is 01:05 UTC.
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 1, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ITaskService _tasks = Substitute.For<ITaskService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly ServerRebootScheduler _scheduler;
    private readonly ServerRebootScheduleService _service;

    public ServerRebootSchedulerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
        var repository = new ServerRebootRepository(_db);
        _scheduler = new ServerRebootScheduler(repository, _tasks, _audit, _clock, NullLogger<ServerRebootScheduler>.Instance);
        _service = new ServerRebootScheduleService(repository, _audit, _clock);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<int> SeedScheduleAsync(string zone = "Europe/Paris", int hour = 2, int minute = 5)
    {
        var server = new Server { Name = "web-01", Hostname = "web-01", OrganizationId = 1, Status = ServerStatus.Online };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(Ct);
        await _service.SaveAsync(server.Id,
            new SaveServerRebootScheduleRequest { Hour = hour, Minute = minute, TimeZoneId = zone, Enabled = true }, "admin", Ct);
        _db.ChangeTracker.Clear();
        return server.Id;
    }

    private Task<List<ServerTask>> OrdersAsync(int serverId) =>
        _db.Tasks.AsNoTracking().Where(task => task.ServerId == serverId && task.Operation == OperationKind.ServerReboot).ToListAsync(Ct);

    private Task<ServerRebootSchedule> ScheduleAsync(int serverId) =>
        _db.ServerRebootSchedules.AsNoTracking().SingleAsync(schedule => schedule.ServerId == serverId, Ct);

    private async Task SeedRunningTaskAsync(int serverId, string name)
    {
        _db.Tasks.Add(new ServerTask { ServerId = serverId, Name = name, Status = TaskExecutionStatus.Running, Operation = OperationKind.PipelineDeploy });
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Nothing_is_ordered_before_the_time_of_the_schedule()
    {
        var serverId = await SeedScheduleAsync();
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 1, 4, 0, TimeSpan.Zero));

        await _scheduler.RunOnceAsync(Ct);

        Assert.Empty(await OrdersAsync(serverId));
    }

    [Fact]
    public async Task The_order_is_emitted_at_the_time_of_the_schedule_in_its_time_zone()
    {
        var serverId = await SeedScheduleAsync();
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 1, 5, 30, TimeSpan.Zero));

        await _scheduler.RunOnceAsync(Ct);
        await _scheduler.RunOnceAsync(Ct);

        var order = Assert.Single(await OrdersAsync(serverId));
        var env = JsonSerializer.Deserialize<Dictionary<string, string>>(order.EnvironmentVariables)!;
        var schedule = await ScheduleAsync(serverId);
        Assert.Equal("1", env[ServerRebootOrder.ScheduleVersionEnvVar]);
        Assert.Equal(new DateTime(2026, 11, 7, 1, 5, 0, DateTimeKind.Utc), DateTime.Parse(env[ServerRebootOrder.OccurrenceEnvVar]).ToUniversalTime());
        Assert.Equal(new DateTime(2026, 11, 7, 2, 5, 0, DateTimeKind.Utc), DateTime.Parse(env[ServerRebootOrder.NotAfterEnvVar]).ToUniversalTime());
        Assert.Equal(RebootScheduleOutcome.Ordered, schedule.LastOutcome);
        Assert.Equal(order.Id, schedule.LastTaskId);
        await _tasks.Received(1).NotifyTaskQueuedAsync(Arg.Is<ServerTask>(task => task.Id == order.Id), null, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("ServerRebootOrdered", "ServerReboot", serverId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_utc_schedule_fires_at_its_utc_time()
    {
        var serverId = await SeedScheduleAsync(zone: ServerRebootOrder.UtcTimeZone, hour: 2, minute: 5);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 1, 6, 0, TimeSpan.Zero));
        await _scheduler.RunOnceAsync(Ct);
        Assert.Empty(await OrdersAsync(serverId));

        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 2, 6, 0, TimeSpan.Zero));
        await _scheduler.RunOnceAsync(Ct);
        Assert.Single(await OrdersAsync(serverId));
    }

    [Fact]
    public async Task A_running_step_postpones_the_reboot_then_it_runs_once_the_server_is_idle()
    {
        var serverId = await SeedScheduleAsync();
        await SeedRunningTaskAsync(serverId, "Deploy - web");
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 1, 5, 30, TimeSpan.Zero));

        await _scheduler.RunOnceAsync(Ct);

        Assert.Empty(await OrdersAsync(serverId));
        var postponed = await ScheduleAsync(serverId);
        Assert.Equal(RebootScheduleOutcome.Postponed, postponed.LastOutcome);
        Assert.Equal("Deploy - web", postponed.OutcomeDetail);

        var running = await _db.Tasks.SingleAsync(task => task.Name == "Deploy - web", Ct);
        running.Status = TaskExecutionStatus.Success;
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 1, 30, 0, TimeSpan.Zero));

        await _scheduler.RunOnceAsync(Ct);

        Assert.Single(await OrdersAsync(serverId));
        Assert.Equal(RebootScheduleOutcome.Ordered, (await ScheduleAsync(serverId)).LastOutcome);
    }

    [Fact]
    public async Task A_server_still_busy_past_the_postponement_skips_the_reboot_with_its_reason()
    {
        var serverId = await SeedScheduleAsync();
        await SeedRunningTaskAsync(serverId, "Build - nightly");
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 1, 5, 30, TimeSpan.Zero));
        await _scheduler.RunOnceAsync(Ct);

        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 2, 6, 0, TimeSpan.Zero));
        await _scheduler.RunOnceAsync(Ct);
        await _scheduler.RunOnceAsync(Ct);

        Assert.Empty(await OrdersAsync(serverId));
        var skipped = await ScheduleAsync(serverId);
        Assert.Equal(RebootScheduleOutcome.Skipped, skipped.LastOutcome);
        Assert.Equal(RebootSkipReason.ServerBusy, skipped.SkipReason);
        Assert.Equal("Build - nightly", skipped.OutcomeDetail);
        await _audit.Received(1).LogAsync("ServerRebootSkipped", "ServerReboot", serverId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_occurrence_aetheus_did_not_see_in_time_is_skipped_as_missed()
    {
        var serverId = await SeedScheduleAsync();
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero));

        await _scheduler.RunOnceAsync(Ct);

        Assert.Empty(await OrdersAsync(serverId));
        var skipped = await ScheduleAsync(serverId);
        Assert.Equal(RebootScheduleOutcome.Skipped, skipped.LastOutcome);
        Assert.Equal(RebootSkipReason.Missed, skipped.SkipReason);
    }

    [Fact]
    public async Task A_disabled_schedule_orders_nothing()
    {
        var serverId = await SeedScheduleAsync();
        await _service.SaveAsync(serverId,
            new SaveServerRebootScheduleRequest { Hour = 2, Minute = 5, TimeZoneId = "Europe/Paris", Enabled = false }, "admin", Ct);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 1, 5, 30, TimeSpan.Zero));

        await _scheduler.RunOnceAsync(Ct);

        Assert.Empty(await OrdersAsync(serverId));
    }

    private async Task<(int ServerId, int TaskId)> OrderedAsync()
    {
        var serverId = await SeedScheduleAsync();
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 1, 5, 30, TimeSpan.Zero));
        await _scheduler.RunOnceAsync(Ct);
        _db.ChangeTracker.Clear();
        return (serverId, Assert.Single(await OrdersAsync(serverId)).Id);
    }

    [Fact]
    public async Task The_current_order_of_an_unchanged_schedule_is_confirmed_and_audited()
    {
        var (serverId, taskId) = await OrderedAsync();

        var answer = await _service.ConfirmOrderAsync(serverId, taskId, Ct);

        Assert.True(answer.Allowed);
        await _audit.Received(1).LogAsync("ServerRebootConfirmed", "ServerReboot", serverId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_order_emitted_before_the_schedule_changed_is_refused()
    {
        var (serverId, taskId) = await OrderedAsync();
        await _service.SaveAsync(serverId,
            new SaveServerRebootScheduleRequest { Hour = 4, Minute = 0, TimeZoneId = "Europe/Paris", Enabled = true }, "admin", Ct);

        var answer = await _service.ConfirmOrderAsync(serverId, taskId, Ct);

        Assert.False(answer.Allowed);
        Assert.Equal(RebootOrderRefusal.ScheduleChanged, answer.Refusal);
        await _audit.Received(1).LogAsync("ServerRebootRefused", "ServerReboot", serverId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_order_of_a_disabled_or_removed_schedule_is_refused()
    {
        var (serverId, taskId) = await OrderedAsync();
        await _service.SaveAsync(serverId,
            new SaveServerRebootScheduleRequest { Hour = 2, Minute = 5, TimeZoneId = "Europe/Paris", Enabled = false }, "admin", Ct);
        Assert.Equal(RebootOrderRefusal.ScheduleDisabled, (await _service.ConfirmOrderAsync(serverId, taskId, Ct)).Refusal);

        _db.ChangeTracker.Clear();
        await _service.DeleteAsync(serverId, Ct);
        Assert.Equal(RebootOrderRefusal.ScheduleRemoved, (await _service.ConfirmOrderAsync(serverId, taskId, Ct)).Refusal);
    }

    [Fact]
    public async Task An_order_past_its_deadline_or_beside_a_running_task_is_refused()
    {
        var (serverId, taskId) = await OrderedAsync();
        await SeedRunningTaskAsync(serverId, "Deploy - web");
        Assert.Equal(RebootOrderRefusal.ServerBusy, (await _service.ConfirmOrderAsync(serverId, taskId, Ct)).Refusal);

        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 2, 6, 0, TimeSpan.Zero));
        Assert.Equal(RebootOrderRefusal.Expired, (await _service.ConfirmOrderAsync(serverId, taskId, Ct)).Refusal);
    }

    [Fact]
    public async Task Another_server_cannot_confirm_an_order_it_does_not_own()
    {
        var (_, taskId) = await OrderedAsync();

        var answer = await _service.ConfirmOrderAsync(agentServerId: 999, taskId, Ct);

        Assert.False(answer.Allowed);
        Assert.Equal(RebootOrderRefusal.NotARebootOrder, answer.Refusal);
    }

    [Fact]
    public async Task An_unknown_time_zone_is_refused()
    {
        var server = new Server { Name = "web-02", Hostname = "web-02", OrganizationId = 1, Status = ServerStatus.Online };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => _service.SaveAsync(server.Id,
            new SaveServerRebootScheduleRequest { Hour = 2, Minute = 5, TimeZoneId = "Mars/Olympus", Enabled = true }, "admin", Ct));
    }

    [Fact]
    public async Task AFailedPass_IsLoggedAndSwallowed_SoTheNextTickRunsAgain()
    {
        // No scheduler registered: resolving it throws, as a broken pass would. The service must survive it.
        var provider = new ServiceCollection().BuildServiceProvider();
        var logger = new ErrorCountingLogger();
        var service = new ServerRebootSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(), logger, TimeProvider.System,
            leaderLease: LeaderLeaseDouble.Leader());

        await service.RunPassAsync(Ct);

        var error = Assert.Single(logger.Errors);
        Assert.IsType<InvalidOperationException>(error);
    }

    private sealed class ErrorCountingLogger : ILogger<ServerRebootSchedulerService>
    {
        public List<Exception?> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Errors.Add(exception);
        }
    }
}
