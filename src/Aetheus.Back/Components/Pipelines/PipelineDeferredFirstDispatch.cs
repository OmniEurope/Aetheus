// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-7: runs the first stage dispatch of a manually launched run after the launch answered. The dispatch
/// launches the child runs of a trigger step inside the request (each one prepares, checks and dispatches
/// its own first stage), which took 77 percent of the answer's time for a pipeline orchestrating three
/// children, and the person who clicked "Exécuter" saw nothing until it ended.
///
/// What it moves is the work, never a check: the dispatch applies the same approvals, environment checks and
/// server selection as before, and a failure ends the run Failed with the reason as a run warning (the run is
/// already on screen, so it cannot be an HTTP error any more). A dispatch the backend never finished (stopped
/// in the middle) leaves a Running run with pending steps and no work in flight, which the scheduler recovery
/// sweep re-drives after its grace.
/// </summary>
public interface IPipelineDeferredFirstDispatch
{
    /// <summary>Starts the first dispatch of <paramref name="runId"/> in the background and returns at once. Its audit
    /// entries name <paramref name="initiatedBy"/>, the person whose request launched the run (null: "system", as the
    /// audit names a request without a user).</summary>
    void Start(
        int runId, int pipelineId, PipelineYamlDefinition definition,
        Dictionary<string, string> resolvedVariables, HashSet<string> secretKeys, string? initiatedBy);

    /// <summary>Completes once every dispatch started so far has ended (shutdown, and the tests that read its
    /// effect).</summary>
    Task WhenIdleAsync();
}

public sealed class PipelineDeferredFirstDispatch(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<PipelineDeferredFirstDispatch> logger) : IPipelineDeferredFirstDispatch
{
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    public void Start(
        int runId, int pipelineId, PipelineYamlDefinition definition,
        Dictionary<string, string> resolvedVariables, HashSet<string> secretKeys, string? initiatedBy)
    {
        // Task.Run, not the request's flow: the request scope ends with the answer, the dispatch brings its own.
        // The request's execution context does not flow either: it carries the HttpContext, which the dispatch would
        // otherwise read (or not, once the request ended) depending on timing. Who the dispatch acts for travels
        // explicitly, as initiatedBy.
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
            dispatch = Task.Run(() => DispatchAsync(runId, pipelineId, definition, resolvedVariables, secretKeys, initiatedBy));
        _running[dispatch] = 0;
        _ = dispatch.ContinueWith(
            finished => _running.TryRemove(finished, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task WhenIdleAsync() => Task.WhenAll(_running.Keys);

    private async Task DispatchAsync(
        int runId, int pipelineId, PipelineYamlDefinition definition,
        Dictionary<string, string> resolvedVariables, HashSet<string> secretKeys, string? initiatedBy)
    {
        var ct = lifetime.ApplicationStopping;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            // The audit of this scope (the child runs the dispatch starts) names who clicked, as inside the click.
            // An audit actor only: the child launches are authorized against their pipeline's owner either way.
            if (!string.IsNullOrWhiteSpace(initiatedBy))
                scope.ServiceProvider.GetRequiredService<IAuditActor>().ActAs(initiatedBy);
            var launcher = scope.ServiceProvider.GetRequiredService<IPipelineRunLauncher>();
            // The same lock as an advancement and a cancellation of the run: until the first stage is dispatched,
            // a cancel must not interleave with the tasks it creates.
            using var runLock = await RunAdvanceLock.Shared.AcquireAsync(runId, ct).ConfigureAwait(false);
            await launcher.DispatchFirstStageAsync(runId, pipelineId, definition, resolvedVariables, secretKeys, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("First dispatch of run {RunId} interrupted by the shutdown; the recovery sweep re-drives it.", runId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Mandatory catch: nobody awaits this task, and the failure to record it on the run is itself a failure.
            logger.LogCritical(ex, "First dispatch of run {RunId} failed and could not be recorded on the run", runId);
        }
    }
}
