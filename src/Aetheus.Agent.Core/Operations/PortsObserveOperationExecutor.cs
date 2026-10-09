// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// PLAN-005 lot 2: the on-demand port scan. The periodic path rides the heartbeat; this one exists so
/// an operator who just stopped a service does not have to wait a slow-inventory period to see it.
///
/// It reports through the agent's own token rather than waiting for the next heartbeat, otherwise the
/// "Refresh" button would return without refreshing anything - a green result for work not done.
/// </summary>
public sealed class PortsObserveOperationExecutor(
    IListeningPortsCollector collector,
    IServerApiClient apiClient,
    AgentState agentState,
    TimeProvider timeProvider,
    ILogger<PortsObserveOperationExecutor> logger) : EnvironmentOperationExecutor
{
    public override bool CanHandle(OperationKind kind) => kind == OperationKind.PortsObserve;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        if (!CanHandle(kind))
        {
            await onOutput($"Unsupported operation {kind}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        if (agentState.ServerId is not { } serverId)
        {
            await onOutput("The agent is not enrolled yet, so it cannot report a port scan.", TaskLogLevel.Error)
                .ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        List<ObservedPortDto> ports;
        try
        {
            ports = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Port observation failed");
            await onOutput("The listening-port scan failed; see the agent logs.", TaskLogLevel.Error)
                .ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        try
        {
            await apiClient.ReportObservedPortsAsync(
                serverId,
                new ObservedPortsReportDto
                {
                    Ports = ports,
                    ObservedAt = timeProvider.GetUtcNow().UtcDateTime
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reporting the observed ports failed");
            await onOutput("The scan ran but its result could not be reported.", TaskLogLevel.Error)
                .ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        await onOutput($"Reported {ports.Count} listening TCP port(s).", TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }
}
