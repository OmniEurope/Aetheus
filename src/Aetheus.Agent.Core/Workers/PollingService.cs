// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using Aetheus.Agent.Core.Functions;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Workers;

public sealed class PollingService(
    IServerApiClient apiClient,
    IEnrollmentService enrollment,
    IEnumerable<IExecutor> executors,
    IEnumerable<IOperationExecutor> operationExecutors,
    IContainerExecutor containerExecutor,
    ContainerPipelineFunctionExecutor containerFunctions,
    IDockerStorageMaintenance dockerStorageMaintenance,
    IDeploymentBuildRefusalOutbox refusalOutbox,
    AgentState agentState,
    AgentRuntimeHealth runtimeHealth,
    IAgentProcessRestarter processRestarter,
    TimeProvider timeProvider,
    IOptions<AetheusAgentOptions> options,
    ILogger<PollingService> logger) : BackgroundService
{
    private readonly AetheusAgentOptions _options = options.Value;
    /// <summary>
    /// How long a claimed build task may wait for the local exclusive build lease before it is handed
    /// back to the queue. Deliberately well under the control plane's two-minute Assigned ceiling, so the
    /// release always wins the race against the watchdog.
    /// </summary>
    internal static readonly TimeSpan BuildLeaseWaitBudget = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _concurrencyGate = new(options.Value.MaxConcurrentTasks);
    internal readonly ConcurrentDictionary<int, TrackedTask> _runningTasks = new();

    // R-12: stop reports still being sent for tasks that already left _runningTasks (the report goes out
    // after the task left the table). Registered before the task leaves it, so the drain never misses one.
    private readonly ConcurrentDictionary<int, Task> _pendingStopReports = new();

    private readonly ConcurrentDictionary<OperationKind, IOperationExecutor?> _operationExecutorCache = new();

    internal sealed record TrackedTask(
        Task Execution, CancellationTokenSource Cts, bool IsSelfUpdate = false, int? PipelineRunId = null);

    private static readonly HashSet<string> TerminalStatuses =
        [nameof(TaskExecutionStatus.Success), nameof(TaskExecutionStatus.Failed),
         nameof(TaskExecutionStatus.Cancelled), nameof(TaskExecutionStatus.Timeout)];

    public override void Dispose()
    {
        _concurrencyGate.Dispose();
        base.Dispose();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (!_runningTasks.IsEmpty)
        {
            logger.LogInformation("Waiting for {Count} in-flight tasks to complete...", _runningTasks.Count);

            var drainTask = Task.WhenAll(_runningTasks.Values.Select(t => t.Execution));
            var shutdownTcs = new TaskCompletionSource();
            await using (cancellationToken.Register(() => shutdownTcs.TrySetResult()).ConfigureAwait(false))
            {
                var winner = await Task.WhenAny(drainTask, shutdownTcs.Task).ConfigureAwait(false);
                if (winner == drainTask)
                    logger.LogInformation("All in-flight tasks drained");
                else
                    logger.LogWarning("Shutdown budget exhausted with {Count} task(s) still running", _runningTasks.Count);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!enrollment.IsEnrolled && !stoppingToken.IsCancellationRequested)
            await Task.Delay(AgentRuntimeDefaults.StartupRetryDelay, timeProvider, stoppingToken).ConfigureAwait(false);

        runtimeHealth.BeginPollingGracePeriod();
        logger.LogInformation("Polling service started (interval: {Interval}s, maxConcurrent: {Max})",
            _options.PollingIntervalSeconds, _options.MaxConcurrentTasks);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(NextPollDelay(), timeProvider, stoppingToken).ConfigureAwait(false);
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Polling failed, will retry next interval");
            }
        }
    }

    /// <summary>Ticks of the last task claim or completion, read and written across task threads.</summary>
    private long _lastTaskActivityTicks = DateTimeOffset.MinValue.UtcTicks;

    private void MarkTaskActivity() =>
        Interlocked.Exchange(ref _lastTaskActivityTicks, timeProvider.GetUtcNow().UtcTicks);

    /// <summary>The configured interval when idle; the active one while a task runs or shortly after
    /// one was claimed or finished. Never slower than the configured interval.</summary>
    internal TimeSpan NextPollDelay()
    {
        var idle = TimeSpan.FromSeconds(_options.PollingIntervalSeconds);
        var sinceActivity = timeProvider.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastTaskActivityTicks);
        var active = !_runningTasks.IsEmpty || sinceActivity < AgentRuntimeDefaults.ActivePollingWindow.Ticks;
        return active && AgentRuntimeDefaults.ActivePollingInterval < idle
            ? AgentRuntimeDefaults.ActivePollingInterval
            : idle;
    }

    // E-3: one poll iteration's work (reconcile + fetch + dispatch), extracted so tests can
    // drive it deterministically without a PeriodicTimer or a real Task.Delay sleep. Dispatch
    // stays fire-and-forget (tracked in _runningTasks); tests await DrainRunningTasksAsync to
    // observe the dispatched task's terminal effect deterministically.
    internal async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        await ReplayDeploymentBuildRefusalsAsync(stoppingToken).ConfigureAwait(false);
        await ReconcileRunningTasksAsync(stoppingToken).ConfigureAwait(false);

        // While a self-update runs the process is about to be replaced, so it asks for nothing
        // else; a task claimed now would be cut by the exit and end as a timeout.
        var freeSlots = _runningTasks.Values.Any(tracked => tracked.IsSelfUpdate)
            ? 0
            : Math.Max(0, _options.MaxConcurrentTasks - _runningTasks.Count);
        var tasks = await apiClient.GetPendingTasksAsync(agentState.ServerId!.Value, freeSlots, stoppingToken).ConfigureAwait(false);
        runtimeHealth.MarkPollingSuccess();

        foreach (var task in tasks)
        {
            if (_runningTasks.ContainsKey(task.Id))
                continue;

            if (task.Operation == OperationKind.AgentSelfUpdate && !_runningTasks.IsEmpty)
            {
                logger.LogInformation(
                    "Self-update task {TaskId} is waiting for {Count} tracked task(s) to finish",
                    task.Id,
                    _runningTasks.Count);
                await DrainRunningTasksAsync().ConfigureAwait(false);
            }

            if (task.Operation == OperationKind.AgentSelfUpdate && !runtimeHealth.IsIdle)
            {
                logger.LogWarning(
                    "Self-update task {TaskId} is waiting for local activity to stop: tasks={TaskCount}, processes={ProcessCount}",
                    task.Id,
                    runtimeHealth.ActiveTaskCount,
                    runtimeHealth.ActiveProcessCount);
                continue;
            }

            if (HasCancelledTaskStillStopping(task))
            {
                // The control plane marks a cancelled step done at once and dispatches the always()/Cleanup
                // stage, but the cancelled step's process tree is still being killed here (the executor
                // awaits it). A second free slot must not start that stage beside it: both would touch the
                // run's workspace and resource locks. The claim goes back to the queue until the step has
                // left the tracking table.
                logger.LogInformation(
                    "Task {TaskId} is returning to the queue: a cancelled task of run {RunId} is still stopping",
                    task.Id, task.PipelineRunId);
                await apiClient.ReleaseTaskAsync(task.Id, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (!await _concurrencyGate.WaitAsync(0, stoppingToken).ConfigureAwait(false))
            {
                // The claim already moved this task to Assigned on the control plane. Dropping it here
                // stranded it there: nothing dispatched it, nothing handed it back, and the start
                // ceiling failed the run minutes later on a task the agent never even logged as
                // starting. The free slots this poll asked for and this gate can disagree for a
                // moment, because a finishing task releases the gate before it leaves the tracking
                // table, so hand the claim back instead of letting that window decide a run's fate.
                logger.LogInformation(
                    "Task {TaskId} is returning to the queue: no free execution slot right now", task.Id);
                await apiClient.ReleaseTaskAsync(task.Id, stoppingToken).ConfigureAwait(false);
                continue;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            // The tracking entry must exist BEFORE execution starts: a synchronously
            // completing execution would otherwise finish before TryAdd, leaving a ghost
            // entry whose disposed CTS then throws on every reconciliation pass.
            var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executionTask = TrackTaskAsync(task, cts, registered.Task, stoppingToken);
            _runningTasks.TryAdd(task.Id, new TrackedTask(
                executionTask, cts, task.Operation == OperationKind.AgentSelfUpdate, task.PipelineRunId));
            MarkTaskActivity();
            registered.SetResult();
        }
    }

    /// <summary>True when a task of the same pipeline run is still tracked after its token was cancelled
    /// (server-side cancellation observed by the reconcile pass): its process tree may still be dying.</summary>
    private bool HasCancelledTaskStillStopping(PendingTaskDto task) =>
        task.PipelineRunId is { } runId
        && _runningTasks.Values.Any(tracked =>
            tracked.PipelineRunId == runId && tracked.Cts.IsCancellationRequested);

    // E-3 test seam: await all currently dispatched in-flight tasks so an assertion on a
    // task's terminal effect (StartTask/CompleteTask) is deterministic rather than time-raced.
    // The tracked tasks are snapshotted first, the stop reports second: a task that left the table in
    // between registered its report before leaving, so one of the two snapshots always holds it.
    internal Task DrainRunningTasksAsync()
    {
        var tracked = _runningTasks.Values.Select(t => t.Execution).ToList();
        return Task.WhenAll(tracked.Concat(_pendingStopReports.Values));
    }

    internal async Task ReconcileRunningTasksAsync(CancellationToken ct)
    {
        if (_runningTasks.IsEmpty) return;

        var trackedIds = _runningTasks.Keys.ToList();
        try
        {
            var statuses = await apiClient.GetTaskStatusesAsync(trackedIds, ct).ConfigureAwait(false);
            foreach (var (taskId, status) in statuses)
            {
                if (!TerminalStatuses.Contains(status)) continue;
                if (!_runningTasks.TryGetValue(taskId, out var tracked)) continue;

                logger.LogWarning("Task {TaskId} was {Status} server-side - cancelling local execution", taskId, status);
                await tracked.Cts.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Task reconciliation failed (non-fatal), will retry next poll");
        }
    }

    private async Task TrackTaskAsync(
        PendingTaskDto task, CancellationTokenSource cts, Task registered, CancellationToken hostStopping)
    {
        await registered.ConfigureAwait(false);
        runtimeHealth.BeginTask();
        // R-33: the window a Testcontainers reaper started by this step's tests is attributed to.
        var pipelineWindow = task.PipelineRunId is null ? null : runtimeHealth.TrackPipelineTask();
        var stoppedByServerCancellation = false;
        TaskCompletionSource? stopReport = null;
        try
        {
            // A cancelled token with the host still running is the reconcile pass obeying the server;
            // a host shutdown leaves the task for the control plane to time out and must not report it.
            stoppedByServerCancellation = await ExecuteTaskAsync(task, cts.Token).ConfigureAwait(false)
                                          && !hostStopping.IsCancellationRequested;
        }
        finally
        {
            // Closed before the task leaves the tracking table: whoever saw it leave (DrainRunningTasksAsync)
            // must find its window closed, not still open until a later disposal.
            pipelineWindow?.Dispose();
            runtimeHealth.EndTask();
            // Every task the server cancelled is reported once its stop is proven, a server operation as much as
            // a pipeline step (ACORE-001): the control plane records that nothing of it remains on this agent.
            if (stoppedByServerCancellation)
            {
                // Registered before the task leaves the table: whoever finds it gone (DrainRunningTasksAsync)
                // still finds its report in flight.
                stopReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingStopReports[task.Id] = stopReport.Task;
            }

            _runningTasks.TryRemove(task.Id, out _);
            MarkTaskActivity();
            cts.Dispose();
        }

        // After the finally: the report says "nothing of this task remains here", so it goes out last.
        if (stopReport is null)
            return;

        try
        {
            await ReportCancelledTaskStoppedAsync(task).ConfigureAwait(false);
        }
        finally
        {
            _pendingStopReports.TryRemove(task.Id, out _);
            stopReport.SetResult();
        }
    }

    /// <summary>Runs the task. Returns true when it ended because its token was cancelled (the server-side
    /// cancellation seen by the reconcile pass, or the host stopping), false when it ran to its own end.</summary>
    private async Task<bool> ExecuteTaskAsync(PendingTaskDto task, CancellationToken ct)
    {
        var cancelled = false;
        try
        {
            logger.LogInformation("Starting task {TaskId}: {Name}", task.Id, task.Name);

            task.EnvironmentVariables["AETHEUS_AGENT_WORK_DIRECTORY"] = _options.WorkDirectory;
            if (OperatingSystem.IsLinux())
                task.EnvironmentVariables[AgentHelperPaths.VariableName] = AgentHelperPaths.LinuxDirectory;
            PendingTaskEnvironmentNormalizer.RehomeMirrorCloneUrls(
                task.EnvironmentVariables,
                _options.ServerUrl,
                logger);

            task.EnvironmentVariables.TryGetValue("AETHEUS_EXECUTION_ROLE", out var executionRole);
            if (task.PipelineRunId.HasValue
                && dockerStorageMaintenance.DeploymentOnly
                && !string.Equals(executionRole, "deploy", StringComparison.OrdinalIgnoreCase))
            {
                var report = new DeploymentBuildRefusalReport
                {
                    IncidentId = Guid.NewGuid(),
                    TaskId = task.Id,
                    // BACK-004: the refusal closes the task, so it carries the claim's lease like a completion.
                    AgentSessionId = task.AgentSessionId,
                    AgentSessionFencingToken = task.AgentSessionFencingToken,
                    OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime,
                    Reason = "Pipeline task refused: a deployment-only agent accepts only stages explicitly classified as deploy."
                };
                await refusalOutbox.StoreAsync(report, CancellationToken.None).ConfigureAwait(false);
                await apiClient.StartTaskAsync(task.Id, ct).ConfigureAwait(false);
                await TryReportDeploymentBuildRefusalAsync(report, CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            if (task.Container is not null)
                await ExecuteContainerAsync(task, ct).ConfigureAwait(false);
            else if (task.Operation != OperationKind.None)
                await ExecuteOperationAsync(task, ct).ConfigureAwait(false);
            else
                await ExecuteShellAsync(task, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // A stop that was not proven is never reported: the control plane waits its maximum delay.
            cancelled = ex is not UnconfirmedCancellationException;
            if (cancelled)
                logger.LogInformation("Task {TaskId} cancelled (server-side or shutdown)", task.Id);
            else
                logger.LogError(
                    "Task {TaskId} cancelled (server-side or shutdown), but its processes could not be proven stopped; it is not reported stopped",
                    task.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task {TaskId} execution error", task.Id);
            await TryReportFailureAsync(task.Id, ex, ct).ConfigureAwait(false);
        }
        finally
        {
            if (task.PurgeWorkspace && task.PipelineRunId is { } pipelineRunId)
                ContainerWorkspacePurger.Purge(_options.WorkDirectory, pipelineRunId, logger);
            _concurrencyGate.Release();
        }

        return cancelled;
    }

    /// <summary>
    /// R-12: tells the control plane that a task it cancelled has stopped, with the ordinary completion
    /// report (status Cancelled). The control plane holds the run's cleanup and always() stages back until
    /// it hears this, so they never start beside the process tree that was still dying. Sent once the task
    /// has left the tracking table and released its slot, so nothing of it remains on this agent.
    /// Best-effort: without it the control plane proceeds after its maximum wait.
    /// </summary>
    private async Task ReportCancelledTaskStoppedAsync(PendingTaskDto task)
    {
        try
        {
            await apiClient.CompleteTaskAsync(task.Id, new TaskResultDto
            {
                TaskId = task.Id,
                Status = TaskExecutionStatus.Cancelled,
                ExitCode = -1
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not report that cancelled task {TaskId} stopped; the control plane proceeds after its maximum wait", task.Id);
        }
    }

    private async Task ReplayDeploymentBuildRefusalsAsync(CancellationToken ct)
    {
        IReadOnlyList<DeploymentBuildRefusalReport> pending;
        try
        {
            pending = await refusalOutbox.ReadAllAsync(ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogError(ex, "Could not read the durable deployment-build-refusal outbox");
            return;
        }

        foreach (var report in pending)
            await TryReportDeploymentBuildRefusalAsync(report, ct).ConfigureAwait(false);
    }

    private async Task TryReportDeploymentBuildRefusalAsync(
        DeploymentBuildRefusalReport report,
        CancellationToken ct)
    {
        try
        {
            await apiClient.ReportDeploymentBuildRefusalAsync(report, ct).ConfigureAwait(false);
            refusalOutbox.Remove(report.IncidentId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                ex,
                "Deployment build refusal {IncidentId} remains in the durable outbox for retry",
                report.IncidentId);
        }
    }

    private static bool ChangesHostServices(OperationKind operation) => operation is
        OperationKind.ServiceStart or OperationKind.ServiceStop or OperationKind.ServiceRestart or
        OperationKind.ServiceEnable or OperationKind.ServiceInstall or OperationKind.ServiceUninstall;

    private async Task ExecuteOperationAsync(PendingTaskDto task, CancellationToken ct)
    {
        // Transition the claimed task before resolving the local handler. Older agents may not know
        // a newly introduced typed operation; without this transition their failure report targets an
        // Assigned task and older backends reject it, leaving the pipeline stuck until its timeout.
        await apiClient.StartTaskAsync(task.Id, ct).ConfigureAwait(false);

        var operationExecutor = _operationExecutorCache.GetOrAdd(
            task.Operation,
            kind => operationExecutors.FirstOrDefault(e => e.CanHandle(kind)));

        if (operationExecutor is null)
        {
            logger.LogError("No operation executor handles {Operation}", task.Operation);
            await ReportFailureAsync(
                task.Id,
                TaskFailureCodes.InfrastructureMismatch,
                $"No operation executor handles '{task.Operation}'.",
                ct).ConfigureAwait(false);
            return;
        }

        await using var logForwarder = new BufferedLogForwarder(task.Id, apiClient, timeProvider, ct);
        PendingTaskEnvironmentNormalizer.NormalizeContainerOperationWorkspace(
            task,
            _options.WorkDirectory);

        // The reboot order names itself to the backend when it asks for its confirmation (R-8).
        if (task.Operation is OperationKind.AiRun or OperationKind.ServerReboot)
            task.EnvironmentVariables["AETHEUS_TASK_ID"] = task.Id.ToString();

        var result = await operationExecutor.ExecuteAsync(
            task.Operation, task.Command, task.EnvironmentVariables, task.TimeoutSeconds, logForwarder.OnOutputAsync, ct).ConfigureAwait(false);

        try
        {
            await logForwarder.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task {TaskId} log flush failed (result preserved)", task.Id);
        }

        var restartAfterSelfUpdate = task.Operation == OperationKind.AgentSelfUpdate
            && result.ExitCode == 0
            && !result.TimedOut;
        // R-119: the inventories the operation changed are marked before the task is reported done, so any beat
        // that follows the report collects them again.
        var changedInventories = CachedInventoryOperations.ChangedBy(task.Operation);
        runtimeHealth.InvalidateInventories(changedInventories);
        try
        {
            await CompleteTaskAsync(task.Id, result, CancellationToken.None).ConfigureAwait(false);
            // Whatever the outcome, the host's services may have changed, and so may the cached inventories
            // (Apache, mail...): the new state is sent now, not at the next tick.
            if (ChangesHostServices(task.Operation) || changedInventories != CachedInventory.None)
                runtimeHealth.RequestHeartbeat();
            // R-41: the installed packages changed, so the CVE watch of the host is reported again.
            if (task.Operation == OperationKind.SystemPackageUpgrade) runtimeHealth.RequestHostPackageReport();
        }
        finally
        {
            // A graceful host shutdown is not a Windows service failure, so SCM may leave the
            // agent stopped forever. Prefer completing the backend task first, but never leave
            // swapped binaries running under the old process merely because the final control-
            // plane acknowledgement failed: the new agent can reconcile after reconnecting.
            if (restartAfterSelfUpdate)
            {
                logger.LogWarning("Agent self-update handoff completed; terminating for service-manager restart");
                processRestarter.Restart("Agent self-update applied; service-manager restart requested.");
            }
        }
    }

    private async Task ExecuteShellAsync(PendingTaskDto task, CancellationToken ct)
    {
        var executor = executors.FirstOrDefault(e => e.Type == task.Executor);
        if (executor is null)
        {
            // Keep the server-side lifecycle valid even when this agent does not support the requested
            // executor: Assigned -> Running -> Failed, never Assigned -> Failed.
            await apiClient.StartTaskAsync(task.Id, ct).ConfigureAwait(false);
            logger.LogError("No executor found for type {Type}", task.Executor);
            await ReportFailureAsync(
                task.Id,
                TaskFailureCodes.InfrastructureMismatch,
                $"No executor is registered for type '{task.Executor}'.",
                ct).ConfigureAwait(false);
            return;
        }

        await using var logForwarder = new BufferedLogForwarder(task.Id, apiClient, timeProvider, ct);

        task.EnvironmentVariables.TryGetValue("AETHEUS_EXECUTION_ROLE", out var executionRole);
        var command = PipelineSecretPlaceholder.Resolve(task.Command, task.EnvironmentVariables);
        var isDockerBuild = dockerStorageMaintenance.IsBuildCommand(command);
        var isBuildWorkload = isDockerBuild
            || string.Equals(executionRole, "build", StringComparison.OrdinalIgnoreCase);
        var buildPrepared = false;
        if (isBuildWorkload)
        {
            try
            {
                // Waiting for the local build/maintenance lease is queue time, not execution time.
                // Start the server-side timeout only once this task can actually invoke its executor.
                // The wait is bounded because the control plane fails an Assigned task that never starts:
                // holding this one until another build finishes would make two build pipelines on one
                // runner mutually exclusive. Past the budget the task goes back to the queue, where a
                // busy online runner is allowed to keep work waiting, and a later poll re-claims it.
                buildPrepared = await dockerStorageMaintenance.PrepareBuildAsync(
                    task.EnvironmentVariables,
                    ct,
                    runDockerMaintenance: isDockerBuild,
                    waitBudget: BuildLeaseWaitBudget).ConfigureAwait(false);
                if (!buildPrepared)
                {
                    logger.LogInformation(
                        "Task {TaskId} is returning to the queue: the local build lease stayed held for {Budget}",
                        task.Id,
                        BuildLeaseWaitBudget);
                    await apiClient.ReleaseTaskAsync(task.Id, ct).ConfigureAwait(false);
                    return;
                }
            }
            catch
            {
                // PrepareBuildAsync releases its lease on failure. Transition before the outer failure
                // reporter completes the task so the backend never sees Assigned -> Failed.
                await apiClient.StartTaskAsync(task.Id, ct).ConfigureAwait(false);
                throw;
            }
        }

        ExecutorResult result;
        try
        {
            await apiClient.StartTaskAsync(task.Id, ct).ConfigureAwait(false);
            result = await executor.ExecuteAsync(
                command, task.EnvironmentVariables, task.TimeoutSeconds, logForwarder.OnOutputAsync, ct).ConfigureAwait(false);
        }
        finally
        {
            if (buildPrepared)
                await dockerStorageMaintenance.ScheduleBuildCompletionAsync(
                    runDockerMaintenance: isDockerBuild,
                    ct: CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            await logForwarder.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task {TaskId} log flush failed (build result preserved)", task.Id);
        }

        await CompleteTaskAsync(task.Id, result, CancellationToken.None).ConfigureAwait(false);

        logger.LogInformation("Task {TaskId} completed: exit code {ExitCode}", task.Id, result.ExitCode);
    }

    private async Task ExecuteContainerAsync(PendingTaskDto task, CancellationToken ct)
    {
        await apiClient.StartTaskAsync(task.Id, ct).ConfigureAwait(false);
        await using var logForwarder = new BufferedLogForwarder(task.Id, apiClient, timeProvider, ct);

        // A type: aetheus step of a container-isolated stage runs its function inside the step
        // container (aetheus-fn); its target is a catalog name, never a script.
        var result = ContainerPipelineFunctionExecutor.Handles(task.Operation)
            ? await containerFunctions.ExecuteAsync(
                task.Container!, task.Operation, task.Command, task.EnvironmentVariables, task.TimeoutSeconds,
                logForwarder.OnOutputAsync, ct).ConfigureAwait(false)
            : await containerExecutor.ExecuteAsync(
                task.Container!, PipelineSecretPlaceholder.Resolve(task.Command, task.EnvironmentVariables),
                task.EnvironmentVariables, task.TimeoutSeconds, logForwarder.OnOutputAsync, ct).ConfigureAwait(false);

        try
        {
            await logForwarder.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task {TaskId} log flush failed (container result preserved)", task.Id);
        }

        await CompleteTaskAsync(task.Id, result, CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Container task {TaskId} completed: exit code {ExitCode}", task.Id, result.ExitCode);
    }

    private async Task CompleteTaskAsync(int taskId, ExecutorResult result, CancellationToken ct)
    {
        var status = result.TimedOut
            ? TaskExecutionStatus.Timeout
            : result.ExitCode == 0 ? TaskExecutionStatus.Success : TaskExecutionStatus.Failed;

        await apiClient.CompleteTaskAsync(taskId, new TaskResultDto
        {
            TaskId = taskId,
            Status = status,
            ExitCode = result.ExitCode,
            FailureCode = result.FailureCode,
            FailureReason = BoundFailureReason(result.FailureReason)
        }, ct).ConfigureAwait(false);
    }

    private async Task ReportFailureAsync(
        int taskId,
        string failureCode,
        string failureReason,
        CancellationToken ct)
    {
        var log = new AppendLogRequest
        {
            TaskId = taskId,
            Level = TaskLogLevel.Error,
            Message = $"[agent:{failureCode}] {failureReason}"
        };
        try
        {
            await BufferedLogForwarder.SendLogBatchAsync(apiClient, [log], ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not publish the terminal diagnostic for task {TaskId}", taskId);
        }

        await apiClient.CompleteTaskAsync(taskId, new TaskResultDto
        {
            TaskId = taskId,
            Status = TaskExecutionStatus.Failed,
            ExitCode = -1,
            FailureCode = failureCode,
            FailureReason = BoundFailureReason(failureReason)
        }, ct).ConfigureAwait(false);
    }

    private async Task TryReportFailureAsync(int taskId, Exception error, CancellationToken _)
    {
        var reason = BuildExecutionFailureReason(error);
        try
        {
            await ReportFailureAsync(
                taskId,
                TaskFailureCodes.ToolError,
                reason,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception innerEx)
        {
            logger.LogError(innerEx, "Failed to report task {TaskId} failure", taskId);
        }
    }

    private static string BuildExecutionFailureReason(Exception error)
    {
        var detail = string.IsNullOrWhiteSpace(error.Message)
            ? "No exception message was provided."
            : error.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return BoundFailureReason(
            $"Agent execution failed before the executor returned ({error.GetType().Name}): {detail}")!;
    }

    // TaskResultDto.FailureReason is capped at 512 characters: a longer reason makes the server
    // refuse the whole completion with HTTP 400, so the task never reports its real outcome.
    internal static string? BoundFailureReason(string? reason)
    {
        const int maxLength = 512;
        return reason is null || reason.Length <= maxLength ? reason : reason[..maxLength];
    }

}
