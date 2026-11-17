// SPDX-License-Identifier: EUPL-1.2
using System.Data.Common;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>A clock the test moves by hand; the scheduler reads it through <see cref="TimeProvider"/>.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset value) => _now = value;
}

/// <summary>Keeps every log entry of the scheduler so a test can read what the engine said.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>Records the text of every command a context sends to PostgreSQL.</summary>
internal sealed class StatementRecorder : DbCommandInterceptor
{
    private readonly List<string> _statements = [];

    public IReadOnlyList<string> Statements
    {
        get { lock (_statements) return [.. _statements]; }
    }

    public void Clear()
    {
        lock (_statements) _statements.Clear();
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        lock (_statements) _statements.Add(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
/// The real <see cref="PipelineSchedulerService"/> over the real <see cref="PipelineAutoTriggerRepository"/>
/// and a real PostgreSQL database, driven tick by tick on a manual clock. Only the run service is a
/// double: it records which pipelines the engine decided to start, nothing more.
/// </summary>
internal sealed class PipelineSchedulerHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<(DateTimeOffset At, int PipelineId)> _triggered = [];
    private readonly List<(DateTimeOffset At, int PipelineId, string Source, Dictionary<string, string>? Variables)> _calls = [];

    public PipelineSchedulerHarness(string connectionString, DateTimeOffset start)
    {
        Clock = new ManualTimeProvider(start);
        var runService = Substitute.For<IPipelineRunService>();
        runService
            .When(service => service.TriggerAutomatedRunAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                lock (_calls)
                {
                    _triggered.Add((Clock.GetUtcNow(), call.ArgAt<int>(0)));
                    _calls.Add((Clock.GetUtcNow(), call.ArgAt<int>(0), call.ArgAt<string>(1), call.ArgAt<Dictionary<string, string>?>(2)));
                }

                var failure = Failure?.Invoke(call.ArgAt<int>(0));
                if (failure is not null) throw failure;
            });

        _provider = new ServiceCollection()
            .AddSingleton<TimeProvider>(Clock)
            .AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(Recorder))
            .AddScoped<IPipelineAutoTriggerRepository, PipelineAutoTriggerRepository>()
            .AddScoped(_ => runService)
            .BuildServiceProvider();
        Scheduler = new PipelineSchedulerService(
            _provider.GetRequiredService<IServiceScopeFactory>(), Logger, Clock,
            leaderLease: LeaderLeaseDouble.Leader());
    }

    public ManualTimeProvider Clock { get; }

    public CapturingLogger<PipelineSchedulerService> Logger { get; } = new();

    public StatementRecorder Recorder { get; } = new();

    public PipelineSchedulerService Scheduler { get; }

    /// <summary>Every start decision so far, with the simulated instant of the tick that took it.</summary>
    public IReadOnlyList<(DateTimeOffset At, int PipelineId)> Triggered => _triggered;

    /// <summary>Every start decision with the trigger source and the variables the engine passed along.</summary>
    public IReadOnlyList<(DateTimeOffset At, int PipelineId, string Source, Dictionary<string, string>? Variables)> Calls => _calls;

    /// <summary>When set, the run service double throws what this returns for a pipeline id, after
    /// recording the call; null means the start succeeds.</summary>
    public Func<int, Exception?>? Failure { get; set; }

    /// <summary>Gives the pipeline a run that is still in flight, as a long run would be.</summary>
    public async Task<int> AddActiveRunAsync(int pipelineId, CancellationToken ct)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = new PipelineRun { PipelineId = pipelineId, Status = PipelineStatus.Running, StartedAt = Clock.GetUtcNow().UtcDateTime };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    /// <summary>Ends a run added by <see cref="AddActiveRunAsync"/>.</summary>
    public async Task EndRunAsync(int runId, CancellationToken ct)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.PipelineRuns.SingleAsync(item => item.Id == runId, ct);
        run.Status = PipelineStatus.Success;
        run.CompletedAt = Clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>One wake-up of the engine at the current simulated instant; returns the SQL it sent.</summary>
    public async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct)
    {
        Recorder.Clear();
        await Scheduler.CheckScheduledPipelinesAsync(ct);
        return Recorder.Statements;
    }

    /// <summary>Moves the clock to <paramref name="instant"/> and wakes the engine there.</summary>
    public async Task<IReadOnlyList<string>> TickAtAsync(DateTimeOffset instant, CancellationToken ct)
    {
        Clock.Set(instant);
        return await TickAsync(ct);
    }

    /// <summary>Moves the clock forward one check interval and wakes the engine.</summary>
    public async Task<IReadOnlyList<string>> NextMinuteAsync(CancellationToken ct)
    {
        Clock.Advance(TimeSpan.FromMinutes(1));
        return await TickAsync(ct);
    }

    /// <summary>Pipeline ids started by ticks since <paramref name="since"/> (exclusive).</summary>
    public int[] TriggeredAfter(DateTimeOffset since) =>
        [.. _triggered.Where(entry => entry.At > since).Select(entry => entry.PipelineId)];

    /// <summary>What the repository's freshness check answers right now, read in its own scope.</summary>
    public async Task<ScheduledPipelinesFingerprint> FingerprintAsync(CancellationToken ct)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPipelineAutoTriggerRepository>()
            .GetScheduledPipelinesFingerprintAsync(ct);
    }

    public async Task<int> AddPipelineAsync(
        string name, string yaml, PipelineTriggerType trigger, CancellationToken ct)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipeline = new Pipeline
        {
            Name = name,
            YamlDefinition = yaml,
            TriggerType = trigger
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return pipeline.Id;
    }

    /// <summary>What a save does to the row: new definition and new trigger; the context stamps
    /// <c>UpdatedAt</c> from the clock, as it does for every writer.</summary>
    public async Task SavePipelineAsync(
        int id, string yaml, PipelineTriggerType trigger, CancellationToken ct)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipeline = await db.Pipelines.SingleAsync(item => item.Id == id, ct);
        pipeline.YamlDefinition = yaml;
        pipeline.TriggerType = trigger;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeletePipelineAsync(int id, CancellationToken ct)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Pipelines.Remove(await db.Pipelines.SingleAsync(item => item.Id == id, ct));
        await db.SaveChangesAsync(ct);
    }

    public void Dispose() => _provider.Dispose();
}
