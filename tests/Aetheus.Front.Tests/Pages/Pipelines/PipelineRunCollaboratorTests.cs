// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public class PipelineRunCollaboratorTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunCollaboratorTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // The gate figures come from the run findings route; none by default.
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto());
    }

    [Fact]
    public async Task ChildAggregator_OneChildFailure_PreservesSuccessfulChildren()
    {
        _handler.SetResponse("api/pipelines/runs/10", System.Net.HttpStatusCode.ServiceUnavailable);
        _handler.SetJsonResponse("api/pipelines/runs/11", new PipelineRunDto
        {
            Id = 11,
            Status = PipelineStatus.Success,
            TestResultSummary = new PipelineTestResultSummaryDto { TotalTests = 4, Passed = 4 }
        });
        var parent = new PipelineRunDto
        {
            Id = 1,
            Steps =
            [
                new PipelineStepRunDto { Id = 1, TriggeredRunId = 10 },
                new PipelineStepRunDto { Id = 2, TriggeredRunId = 11 }
            ]
        };

        var result = await PipelineRunChildAggregator.LoadAsync(
            parent, Services.GetRequiredService<ApiClient>(), ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Single(result.Children);
        Assert.True(result.Children.ContainsKey(11));
        Assert.Equal(4, result.Run.TestResultSummary?.TotalTests);
    }

    /// <summary>Each run's summary lists a bounded number of findings, so the merged figures are
    /// the database's over the whole tree (a finding seen by two runs counts once), read in one
    /// request for every run of the tree, not a count of what the summaries list.</summary>
    [Fact]
    public async Task GateState_TakesItsCountsFromTheRunFindingsRoute_OverTheWholeTree()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto
        {
            OpenCount = 2,
            NewOpenCount = 1,
            DecidedCount = 0
        });

        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/1/result", new AnalysisRunGateDto
        {
            PipelineRunId = 1,
            Status = AnalysisGateStatus.Warning,
            Grade = Graded(AnalysisGrade.A, AnalysisGradeDomain.CodeQuality),
            FindingCount = 700,
            NewFindingCount = 120,
            Findings = [new AnalysisRunGateFindingDto { FindingId = 10, IsNew = true }]
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/2/result", new AnalysisRunGateDto
        {
            PipelineRunId = 2,
            Status = AnalysisGateStatus.Passed,
            Grade = Graded(AnalysisGrade.F, AnalysisGradeDomain.Security),
            FindingCount = 800,
            NewFindingCount = 80,
            Findings =
            [
                new AnalysisRunGateFindingDto { FindingId = 10 },
                new AnalysisRunGateFindingDto { FindingId = 11 }
            ]
        });
        var state = new PipelineRunGateState();

        await state.LoadAsync(
            Services.GetRequiredService<ApiClient>(),
            new PipelineRunDto { Id = 1, Status = PipelineStatus.Success },
            [new PipelineRunDto { Id = 2, Status = PipelineStatus.Success }],
            ct: Xunit.TestContext.Current.CancellationToken);

        Assert.NotNull(state.Result);
        Assert.Equal(2, state.Result.FindingCount);
        Assert.Equal(1, state.Result.NewFindingCount);
        Assert.Equal(0, state.Result.DecidedFindingCount);
        Assert.Equal([1, 2], state.RunIds);
        var countRequest = Assert.Single(_handler.Requests, request => request.Url.Contains("api/analysis/runs/findings", StringComparison.Ordinal)).Url;
        Assert.Contains("runIds=1", countRequest, StringComparison.Ordinal);
        Assert.Contains("runIds=2", countRequest, StringComparison.Ordinal);
        Assert.Equal(AnalysisGrade.F, state.Result.Grade?.OverallGrade);
    }

    /// <summary>Domain-wise merging retains the complete security grade from the security run
    /// instead of an incomplete value from another child.</summary>
    [Fact]
    public async Task GateState_MergesTheTreeDomainByDomain_SoTheSecurityRunGradesSecurity()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/1/result", new AnalysisRunGateDto
        {
            PipelineRunId = 1,
            Status = AnalysisGateStatus.Warning,
            Grade = new AnalysisGradeSummaryDto
            {
                OverallGrade = AnalysisGrade.A,
                Completeness = AnalysisGradeCompleteness.Complete,
                Domains =
                [
                    Domain(AnalysisGradeDomain.CodeQuality, AnalysisGrade.A, required: true),
                    new AnalysisGradeDomainDto { Domain = AnalysisGradeDomain.Security, Completeness = AnalysisGradeCompleteness.Incomplete }
                ]
            }
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/2/result", new AnalysisRunGateDto
        {
            PipelineRunId = 2,
            Status = AnalysisGateStatus.Passed,
            Grade = Graded(AnalysisGrade.A, AnalysisGradeDomain.Security)
        });
        // The candidate itself publishes no report: its gate is empty, as #2571's.
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/3/result", new AnalysisRunGateDto
        {
            PipelineRunId = 3,
            Status = AnalysisGateStatus.Error
        });
        var state = new PipelineRunGateState();

        await state.LoadAsync(
            Services.GetRequiredService<ApiClient>(),
            new PipelineRunDto { Id = 3, Status = PipelineStatus.Success },
            [new PipelineRunDto { Id = 1, Status = PipelineStatus.Success }, new PipelineRunDto { Id = 2, Status = PipelineStatus.Success }],
            ct: Xunit.TestContext.Current.CancellationToken);

        var security = Assert.Single(state.Result!.Grade!.Domains, domain => domain.Domain == AnalysisGradeDomain.Security);
        Assert.Equal(AnalysisGradeCompleteness.Complete, security.Completeness);
        Assert.Equal(AnalysisGrade.A, security.Grade);
        Assert.True(security.Required);
        Assert.Equal(AnalysisGrade.A, state.Result.Grade.OverallGrade);
        Assert.Equal(AnalysisGradeCompleteness.Complete, state.Result.Grade.Completeness);
    }

    /// <summary>The gate shows the letter the run is listed with, and says so when the candidate's
    /// seal set it rather than the analysis.</summary>
    [Fact]
    public void GatePresentation_HeadlineGradeIsTheListedRunGrade()
    {
        var gate = new AnalysisRunGateDto { Grade = Graded(AnalysisGrade.A, AnalysisGradeDomain.CodeQuality) };

        Assert.Equal(AnalysisGrade.F, PipelineRunGatePresentation.HeadlineGrade(gate, AnalysisGrade.F));
        Assert.True(PipelineRunGatePresentation.GradeComesFromSeal(gate, AnalysisGrade.F));
        Assert.Equal(AnalysisGrade.A, PipelineRunGatePresentation.HeadlineGrade(gate, null));
        Assert.False(PipelineRunGatePresentation.GradeComesFromSeal(gate, null));
        Assert.False(PipelineRunGatePresentation.GradeComesFromSeal(gate, AnalysisGrade.A));
    }

    private static AnalysisGradeDomainDto Domain(AnalysisGradeDomain domain, AnalysisGrade grade, bool required) => new()
    {
        Domain = domain,
        Grade = grade,
        Required = required,
        Completeness = AnalysisGradeCompleteness.Complete
    };

    private static AnalysisGradeSummaryDto Graded(AnalysisGrade grade, AnalysisGradeDomain domain) => new()
    {
        OverallGrade = grade,
        Completeness = AnalysisGradeCompleteness.Complete,
        Domains = [Domain(domain, grade, required: true)]
    };

    /// <summary>Figures that could not be read are not shown as checked.</summary>
    [Fact]
    public async Task GateState_UnreadableCounts_LeaveTheGateIncomplete()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/1/result", new AnalysisRunGateDto
        {
            PipelineRunId = 1,
            Status = AnalysisGateStatus.Passed,
            FindingCount = 3,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A }
        });
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/findings", System.Net.HttpStatusCode.InternalServerError);
        var state = new PipelineRunGateState();

        await state.LoadAsync(
            Services.GetRequiredService<ApiClient>(),
            new PipelineRunDto { Id = 1, Status = PipelineStatus.Success }, [],
            ct: Xunit.TestContext.Current.CancellationToken);

        Assert.True(state.IsIncomplete);
        Assert.Equal(AnalysisGateStatus.Error, state.Result?.Status);
        Assert.Null(state.Result?.Grade);
    }

    /// <summary>Reverting a decision refreshes tree totals and invalidates cached gates without
    /// loading every run result.</summary>
    [Fact]
    public async Task GateState_ReopenedFinding_ReadsTheCountsAgain_AndForgetsTheCachedGates()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/1/result", new AnalysisRunGateDto
        {
            PipelineRunId = 1,
            Status = AnalysisGateStatus.Warning,
            FindingCount = 1,
            DecidedFindingCount = 1
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto { OpenCount = 1, DecidedCount = 1 });
        var api = Services.GetRequiredService<ApiClient>();
        var cache = new PipelineRunPageCache();
        var run = new PipelineRunDto { Id = 1, Status = PipelineStatus.Success };
        var state = new PipelineRunGateState();
        await state.LoadAsync(api, run, [], cache, Xunit.TestContext.Current.CancellationToken);
        Assert.True(cache.TryGetGate(1, out _));

        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto { OpenCount = 2, NewOpenCount = 1, DecidedCount = 0 });
        await state.ReopenFindingAsync(api, cache, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal((2, 1, 0), (state.Result!.FindingCount, state.Result.NewFindingCount, state.Result.DecidedFindingCount));
        Assert.False(cache.TryGetGate(1, out _));
        Assert.Single(_handler.Requests, request => request.Url.Contains("api/analysis/runs/1/result", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    public async Task GateState_FailedChildLoad_MarksPartialResultIncomplete(
        System.Net.HttpStatusCode statusCode)
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/1/result", new AnalysisRunGateDto
        {
            PipelineRunId = 1,
            Status = AnalysisGateStatus.Passed,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A }
        });
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/2/result", statusCode);
        var state = new PipelineRunGateState();

        await state.LoadAsync(
            Services.GetRequiredService<ApiClient>(),
            new PipelineRunDto { Id = 1, Status = PipelineStatus.Success },
            [new PipelineRunDto { Id = 2, Status = PipelineStatus.Success }],
            ct: Xunit.TestContext.Current.CancellationToken);

        Assert.True(state.IsIncomplete);
        Assert.Equal([2], state.FailedRunIds);
        Assert.Equal(AnalysisGateStatus.Error, state.Result?.Status);
        Assert.Null(state.Result?.Grade);
    }

    [Fact]
    public async Task ChildAggregator_LoadsGrandchildrenAndBubblesTheirResults()
    {
        _handler.SetJsonResponse("api/pipelines/runs/10", new PipelineRunDto
        {
            Id = 10,
            Status = PipelineStatus.Running,
            Steps = [new PipelineStepRunDto { Id = 10, TriggeredRunId = 11 }]
        });
        _handler.SetJsonResponse("api/pipelines/runs/11", new PipelineRunDto
        {
            Id = 11,
            Status = PipelineStatus.Running,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 11,
                    TaskId = 111,
                    Status = TaskExecutionStatus.Running
                }
            ],
            TestResultSummary = new PipelineTestResultSummaryDto { TotalTests = 3, Passed = 3 }
        });
        var parent = new PipelineRunDto
        {
            Id = 1,
            Steps = [new PipelineStepRunDto { Id = 1, TriggeredRunId = 10 }]
        };

        var result = await PipelineRunChildAggregator.LoadAsync(
            parent,
            Services.GetRequiredService<ApiClient>(),
            ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Equal([10, 11], result.Children.Keys.Order().ToArray());
        Assert.Equal(3, result.Run.TestResultSummary?.TotalTests);
        Assert.Contains(PipelineRunPresentation.AllSteps([], result.Children), step => step.TaskId == 111);
        Assert.Equal([10, 11], PipelineRun.LiveChildRunIds(result.Children).Order().ToArray());
    }

    [Fact]
    public async Task ChildAggregator_ManyChildren_BoundsConcurrentRequests()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var requestCount = 0;
        var concurrencyGate = new object();
        // Every read is held until four are in flight at once, then all are released: the overlap is
        // made, not hoped for from a 20 ms pause that a loaded machine could let finish one by one.
        var started = new StateSignal();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(
            HttpMethod.Get,
            "api/pipelines/runs/",
            async ct =>
            {
                Interlocked.Increment(ref requestCount);
                var current = Interlocked.Increment(ref inFlight);
                lock (concurrencyGate) maxInFlight = Math.Max(maxInFlight, current);
                started.Pulse();
                try
                {
                    await release.Task.WaitAsync(ct);
                    return new PipelineRunDto { Id = current };
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            });
        var parent = CreateParentWithChildren(12);

        var load = PipelineRunChildAggregator.LoadAsync(
            parent, Services.GetRequiredService<ApiClient>(), ct: Xunit.TestContext.Current.CancellationToken);
        await started.WhenAsync(() => Volatile.Read(ref inFlight) >= 4, "four reads in flight at once");
        release.SetResult();
        await HangGuard.AwaitAsync(load, "the children loaded");

        Assert.InRange(maxInFlight, 2, 4);
        Assert.Equal(12, requestCount);
    }

    [Fact]
    public async Task ChildAggregator_ReusesTerminalRunFromShortLivedPageCache()
    {
        _handler.SetJsonResponse("api/pipelines/runs/101", new PipelineRunDto
        {
            Id = 101,
            Status = PipelineStatus.Success
        });
        var parent = CreateParentWithChildren(1);
        var cache = new PipelineRunPageCache();

        await PipelineRunChildAggregator.LoadAsync(
            parent, Services.GetRequiredService<ApiClient>(), cache,
            Xunit.TestContext.Current.CancellationToken);
        await PipelineRunChildAggregator.LoadAsync(
            parent, Services.GetRequiredService<ApiClient>(), cache,
            Xunit.TestContext.Current.CancellationToken);

        Assert.Single(_handler.Requests, request => request.Url.Contains("api/pipelines/runs/101"));
    }

    [Fact]
    public async Task GateState_ReusesTerminalGateFromShortLivedPageCache()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/1/result", new AnalysisRunGateDto
        {
            PipelineRunId = 1,
            Status = AnalysisGateStatus.Passed,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A }
        });
        var run = new PipelineRunDto { Id = 1, Status = PipelineStatus.Success };
        var cache = new PipelineRunPageCache();
        var state = new PipelineRunGateState();

        await state.LoadAsync(
            Services.GetRequiredService<ApiClient>(), run, [], cache,
            Xunit.TestContext.Current.CancellationToken);
        await state.LoadAsync(
            Services.GetRequiredService<ApiClient>(), run, [], cache,
            Xunit.TestContext.Current.CancellationToken);

        Assert.Single(_handler.Requests, request => request.Url.Contains("api/analysis/runs/1/result"));
    }

    [Fact]
    public void PageCache_ReusesOnlyTerminalRunsForTwentySeconds()
    {
        var time = new FakeTimeProvider();
        var cache = new PipelineRunPageCache(time);
        cache.StoreRun(new PipelineRunDto { Id = 1, Status = PipelineStatus.Running });

        Assert.False(cache.TryGetRun(1, out _));

        cache.StoreRun(new PipelineRunDto { Id = 1, Status = PipelineStatus.Success });
        Assert.True(cache.TryGetRun(1, out _));

        time.Advance(TimeSpan.FromSeconds(21));
        Assert.False(cache.TryGetRun(1, out _));
    }

    [Fact]
    public async Task GateState_DoesNotCacheGateWithMissingProducers()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/1/result", new AnalysisRunGateDto
        {
            PipelineRunId = 1,
            Status = AnalysisGateStatus.Error,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A },
            MissingProducers = ["dependency-track:pending"]
        });
        var run = new PipelineRunDto { Id = 1, Status = PipelineStatus.Success };
        var cache = new PipelineRunPageCache();
        var state = new PipelineRunGateState();

        await state.LoadAsync(Services.GetRequiredService<ApiClient>(), run, [], cache,
            Xunit.TestContext.Current.CancellationToken);
        await state.LoadAsync(Services.GetRequiredService<ApiClient>(), run, [], cache,
            Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(2, _handler.Requests.Count(request => request.Url.Contains("api/analysis/runs/1/result")));
    }

    [Fact]
    public async Task ChildAggregator_Cancellation_CancelsPendingRequests()
    {
        var startedRequests = 0;
        var firstRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(
            HttpMethod.Get,
            "api/pipelines/runs/",
            async ct =>
            {
                Interlocked.Increment(ref startedRequests);
                firstRequestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new PipelineRunDto();
            });
        using var cancellation = new CancellationTokenSource();
        var load = PipelineRunChildAggregator.LoadAsync(
            CreateParentWithChildren(12), Services.GetRequiredService<ApiClient>(), ct: cancellation.Token);
        await HangGuard.AwaitAsync(firstRequestStarted.Task, "the first child read started");

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.InRange(startedRequests, 1, 4);
    }

    private static PipelineRunDto CreateParentWithChildren(int count) => new()
    {
        Id = 1,
        Steps = Enumerable.Range(1, count)
            .Select(id => new PipelineStepRunDto { Id = id, TriggeredRunId = id + 100 })
            .ToList()
    };

    [Fact]
    public async Task LiveConnection_DisposeCancelsScheduledReload()
    {
        var reloadCount = 0;
        var clock = new ObservedFakeClock(DateTimeOffset.UnixEpoch);
        var debounce = TimeSpan.FromMilliseconds(250);
        var connection = new PipelineRunLiveConnection(
            Services.GetRequiredService<HubConnectionFactory>(),
            NullLogger.Instance,
            1,
            () => [],
            () => { reloadCount++; return Task.CompletedTask; },
            () => Task.CompletedTask,
            callback => callback(),
            timeProvider: clock);
        var schedule = typeof(PipelineRunLiveConnection).GetMethod("ScheduleReloadAsync", Priv)!;

        await (Task)schedule.Invoke(connection, [])!;
        Assert.Equal(1, clock.Pending(debounce));
        await connection.DisposeAsync();

        // The scheduled reload's delay was cancelled with the connection: moving past it runs nothing.
        Assert.Equal(0, clock.Pending(debounce));
        clock.Advance(debounce);
        Assert.Equal(0, reloadCount);
    }

    [Fact]
    public async Task LiveConnection_StepStartedReloadsAndDiscoversNewChildGroup()
    {
        var childRunIds = new List<int>();
        var reloadCount = 0;
        // The reload runs on the connection's own thread while the test reads its effects on another,
        // so the completion is signalled AFTER the work rather than polled on a counter: waiting on the
        // counter could observe the increment before the child id it publishes, and the assertion then
        // failed with [1] instead of [1, 10] under a loaded suite run.
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new PipelineRunLiveConnection(
            Services.GetRequiredService<HubConnectionFactory>(),
            NullLogger.Instance,
            1,
            () => { lock (childRunIds) return [.. childRunIds]; },
            () =>
            {
                lock (childRunIds)
                {
                    reloadCount++;
                    childRunIds.Add(10);
                }
                reloaded.TrySetResult();
                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            callback => callback());

        await connection.HandleStepStartedAsync(stepId: 99);
        await reloaded.Task.WaitAsync(Aetheus.Front.Tests.TestDoubles.HangGuard.Limit, Xunit.TestContext.Current.CancellationToken);

        lock (childRunIds) Assert.Equal(1, reloadCount);
        Assert.Equal([1, 10], connection.DesiredRunIds().Order().ToArray());
        await connection.DisposeAsync();
    }

    [Fact]
    public void LiveChildRunIds_ExcludesTerminalChildrenFromSignalRGroups()
    {
        var children = new Dictionary<int, PipelineRunDto>
        {
            [10] = new() { Id = 10, Status = PipelineStatus.Running },
            [11] = new() { Id = 11, Status = PipelineStatus.Pending },
            [12] = new() { Id = 12, Status = PipelineStatus.Success },
            [13] = new() { Id = 13, Status = PipelineStatus.Failed }
        };

        var live = PipelineRun.LiveChildRunIds(children);

        Assert.Equal([10, 11], live.Order().ToArray());
    }

    [Fact]
    public void AllStepsAcrossChildren_IncludesRunningChildStepForLiveSelection()
    {
        var parent = new PipelineRunDto
        {
            Id = 1,
            Status = PipelineStatus.Running,
            Steps = [new PipelineStepRunDto { Id = 1, StepName = "trigger", TriggeredRunId = 10 }]
        };
        var child = new PipelineRunDto
        {
            Id = 10,
            Status = PipelineStatus.Running,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 99,
                    StepName = "child-running",
                    TaskId = 500,
                    Status = TaskExecutionStatus.Running
                }
            ]
        };
        var steps = PipelineRunPresentation.AllSteps(
            RunStageBuilder.Build(parent),
            new Dictionary<int, PipelineRunDto> { [10] = child }).ToList();

        Assert.Contains(steps, step => step.Id == 99 && step.TaskId == 500);
    }

    [Fact]
    public async Task LogStreamer_FallbackIsSingleAndRejectsLateTaskLines()
    {
        _handler.SetJsonResponse("api/logs/task/20", new List<TaskLogDto>());
        var window = new PipelineRunLogWindow();
        var cache = window.Lines;
        var streamer = new PipelineRunLogStreamer(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<HubConnectionFactory>(),
            NullLogger.Instance,
            window,
            callback => callback(),
            () => { });
        var selected = new PipelineStepRunDto
        {
            Id = 1,
            TaskId = 20,
            Status = TaskExecutionStatus.Running
        };

        await streamer.UpdateAsync(selected);
        var firstPoll = typeof(PipelineRunLogStreamer).GetField("_pollTask", Priv)!.GetValue(streamer);
        await streamer.UpdateAsync(selected);
        var secondPoll = typeof(PipelineRunLogStreamer).GetField("_pollTask", Priv)!.GetValue(streamer);
        Assert.Same(firstPoll, secondPoll);

        var append = typeof(PipelineRunLogStreamer).GetMethod("AppendStreamedLogsAsync", Priv)!;
        await (Task)append.Invoke(streamer,
            [new[] { new TaskLogDto { TaskId = 19, Message = "late" } }])!;
        Assert.Empty(cache);

        await (Task)append.Invoke(streamer,
            [new[] { new TaskLogDto { TaskId = 20, Message = "current" } }])!;
        Assert.Single(cache[20]);
        await streamer.DisposeAsync();
        await (Task)append.Invoke(streamer,
            [new[] { new TaskLogDto { TaskId = 20, Message = "after dispose" } }])!;
        Assert.Single(cache[20]);
    }

    [Fact]
    public void LogSnapshotMerge_RetainsLineReceivedAfterHubJoinWhileSnapshotWasInFlight()
    {
        var snapshot = new[]
        {
            new TaskLogDto { Id = 1, TaskId = 20, Message = "history", Timestamp = DateTime.UtcNow }
        };
        var streamedWhileReading = new[]
        {
            new TaskLogDto { Id = 2, TaskId = 20, Message = "between join and snapshot", Timestamp = DateTime.UtcNow.AddSeconds(1) }
        };

        var window = new PipelineRunLogWindow();
        window.AppendStreamed(20, streamedWhileReading);
        window.ApplySnapshot(20, snapshot, PipelineRunLogWindow.LiveWindow);
        var merged = window.Lines[20];

        Assert.Equal([1, 2], merged.Select(log => log.Id));
        Assert.Contains(merged, log => log.Message == "between join and snapshot");
    }

    [Fact]
    public void PendingReason_UsesApprovalStateInsteadOfServerAssignment()
    {
        var localizer = new BunitTestHelper.StubLocalizer();
        var step = new PipelineStepRunDto
        {
            StageName = "deploy",
            ServerName = "agent-1",
            Status = TaskExecutionStatus.Pending
        };
        var stages = new List<StageViewModel>
        {
            new() { Name = "deploy", Status = TaskExecutionStatus.Pending, Steps = [step] }
        };

        var ordinary = PipelineRunLogView.GetPendingReason(
            step, stages, new PipelineRunDto { Status = PipelineStatus.Running }, localizer);
        var awaitingApproval = PipelineRunLogView.GetPendingReason(
            step, stages, new PipelineRunDto
            {
                Status = PipelineStatus.Running,
                Approvals = [new PipelineApprovalDto { StageName = "deploy", Status = ApprovalStatus.Pending }]
            }, localizer);

        Assert.Equal("WaitingForExecution", ordinary);
        Assert.Equal("WaitingForApproval", awaitingApproval);
    }

    [Theory]
    [InlineData(true, "Cleanup", TaskExecutionStatus.Success, "agent-1", PipelineStatus.Running, "WaitingForPreviousStage")]
    [InlineData(true, "deploy", TaskExecutionStatus.Success, "agent-1", PipelineStatus.Running, "WaitingForAgent")]
    [InlineData(false, "deploy", TaskExecutionStatus.Pending, "agent-1", PipelineStatus.Running, "WaitingForPreviousStage")]
    [InlineData(false, "deploy", TaskExecutionStatus.Running, null, PipelineStatus.Running, "WaitingForPreviousStage")]
    [InlineData(false, "deploy", TaskExecutionStatus.Success, null, PipelineStatus.Running, "WaitingForAgent")]
    [InlineData(false, "deploy", TaskExecutionStatus.Success, "agent-1", PipelineStatus.WaitingForApproval, "WaitingForApproval")]
    [InlineData(false, "build", TaskExecutionStatus.Pending, "agent-1", PipelineStatus.Running, "WaitingForExecution")]
    public void PendingReason_NamesWhatTheStepWaitsFor(
        bool isSystem, string stageName, TaskExecutionStatus previousStageStatus, string? serverName,
        PipelineStatus runStatus, string expected)
    {
        var step = new PipelineStepRunDto { StageName = stageName, ServerName = serverName, IsSystem = isSystem, Status = TaskExecutionStatus.Pending };
        var stages = new List<StageViewModel>
        {
            new() { Name = "build", Status = previousStageStatus },
            new() { Name = "deploy", Status = TaskExecutionStatus.Pending },
        };

        var reason = PipelineRunLogView.GetPendingReason(
            step, stages, new PipelineRunDto { Status = runStatus }, new BunitTestHelper.StubLocalizer());

        Assert.Equal(expected, reason);
    }

    [Fact]
    public void PendingReason_AnApprovalOfAnotherStage_DoesNotHoldThisOne()
    {
        var step = new PipelineStepRunDto { StageName = "deploy", ServerName = "agent-1", Status = TaskExecutionStatus.Pending };
        var run = new PipelineRunDto
        {
            Status = PipelineStatus.WaitingForApproval,
            Approvals =
            [
                new PipelineApprovalDto { StageName = "release", Status = ApprovalStatus.Pending },
                new PipelineApprovalDto { StageName = "deploy", Status = ApprovalStatus.Approved },
            ]
        };

        var reason = PipelineRunLogView.GetPendingReason(
            step, [new StageViewModel { Name = "deploy" }], run, new BunitTestHelper.StubLocalizer());

        Assert.Equal("WaitingForExecution", reason);
        Assert.Equal("WaitingForAgent", PipelineRunLogView.GetPendingReason(
            step with { ServerName = null }, [], null, new BunitTestHelper.StubLocalizer()));
    }

    [Fact]
    public void EmptyLogsReason_ExplainsConditionThatPreventedExecution()
    {
        var localizer = new BunitTestHelper.StubLocalizer();
        var step = new PipelineStepRunDto
        {
            Status = TaskExecutionStatus.Cancelled,
            SkippedCondition = "eq(variables['AETHEUS_GITLEAKS_HISTORY'], 'true')",
            SkippedConditionVariables =
            {
                ["AETHEUS_GITLEAKS_HISTORY"] = "false"
            }
        };

        var reason = PipelineRunLogView.GetEmptyLogsReason(step, localizer);

        Assert.Equal("LogsConditionNotMet", reason);
    }

    [Fact]
    public void EmptyLogsReason_ShowsStructuredAgentFailure()
    {
        var localizer = new BunitTestHelper.StubLocalizer();
        var step = new PipelineStepRunDto
        {
            TaskId = 42,
            Status = TaskExecutionStatus.Failed,
            ExitCode = -1,
            FailureCode = "ToolError",
            FailureReason = "Agent executor crashed."
        };

        var reason = PipelineRunLogView.GetEmptyLogsReason(step, localizer);

        Assert.Equal("LogsFailureDiagnostic", reason);
    }

    [Fact]
    public void ApplyQueueState_UpdatesAssignedStepsWithoutChangingRunState()
    {
        var run = new PipelineRunDto
        {
            Id = 9,
            Status = PipelineStatus.Running,
            Steps =
            [
                new PipelineStepRunDto { Id = 1, TaskId = 11, Status = TaskExecutionStatus.Assigned },
                new PipelineStepRunDto { Id = 2, TaskId = 12, Status = TaskExecutionStatus.Running }
            ]
        };
        var queue = new PipelineRunQueueStateDto
        {
            RunId = 9,
            Steps = [new PipelineStepQueueStateDto { StepId = 1, TaskId = 11, Position = 3, Depth = 6 }]
        };

        var updated = PipelineRunQueueRefresh.Apply(run, queue);

        Assert.Equal(PipelineStatus.Running, updated.Status);
        Assert.Equal(3, updated.Steps[0].QueuePosition);
        Assert.Equal(6, updated.Steps[0].QueueDepth);
        Assert.Null(updated.Steps[1].QueuePosition);
    }

    [Fact]
    public void EmptyLogsReason_ShowsStructuredSchedulerFailureWithoutTask()
    {
        var localizer = new BunitTestHelper.StubLocalizer();
        var step = new PipelineStepRunDto
        {
            Status = TaskExecutionStatus.Failed,
            ExitCode = -1,
            FailureCode = "InfrastructureMismatch",
            FailureReason = "No online runner matched stage 'Build'."
        };

        var reason = PipelineRunLogView.GetEmptyLogsReason(step, localizer);

        Assert.Equal("LogsFailureDiagnostic", reason);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Success, "LogsSuccessfulNoOutput")]
    [InlineData(TaskExecutionStatus.Failed, "LogsFailedNoOutput")]
    [InlineData(TaskExecutionStatus.Timeout, "LogsTimeoutNoOutput")]
    [InlineData(TaskExecutionStatus.Cancelled, "LogsCancelledNoOutput")]
    [InlineData(TaskExecutionStatus.Running, "LogsAwaitingOutput")]
    public void EmptyLogsReason_ExplainsTerminalOrActiveTaskState(
        TaskExecutionStatus status,
        string expectedResource)
    {
        var localizer = new BunitTestHelper.StubLocalizer();
        var step = new PipelineStepRunDto
        {
            TaskId = 42,
            Status = status,
            ExitCode = status == TaskExecutionStatus.Success ? 0 : -1
        };

        var reason = PipelineRunLogView.GetEmptyLogsReason(step, localizer);

        Assert.Equal(expectedResource, reason);
    }

    [Fact]
    public void QueueReason_UsesLivePositionWhenAvailable()
    {
        var localizer = new BunitTestHelper.StubLocalizer();
        var step = new PipelineStepRunDto
        {
            Status = TaskExecutionStatus.Assigned,
            ServerName = "runner-1",
            QueuePosition = 3,
            QueueDepth = 8
        };

        var reason = PipelineRunLogView.GetQueueReason(step, localizer);

        Assert.Equal("TaskQueuePositionLive", reason);
    }
}
