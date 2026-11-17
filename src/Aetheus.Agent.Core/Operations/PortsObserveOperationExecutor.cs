// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Run and report an on-demand port scan with the agent token so refresh completes without
/// waiting for periodic heartbeat inventory.</summary>
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

        ListeningPortsScan scan;
        try
        {
            scan = await collector.ScanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Port observation failed");
            await onOutput("The listening-port scan failed; see the agent logs.", TaskLogLevel.Error)
                .ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        // Neither probe worked: an empty listing is no reading, and reporting it would erase every
        // observation the registry holds. The task fails instead of claiming "nothing listens".
        if (!scan.HasMeasurement)
        {
            await onOutput("The TCP and UDP listings could not be read; the observed ports were left unchanged.", TaskLogLevel.Error)
                .ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var ports = scan.Ports;
        var now = timeProvider.GetUtcNow();
        try
        {
            await apiClient.ReportObservedPortsAsync(
                serverId,
                new ObservedPortsReportDto
                {
                    Ports = [.. ports],
                    ObservedAt = now.UtcDateTime,
                    Protocols = scan.ProtocolReport(now)
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

        var measured = string.Join("/", scan.Measured.Select(measurement => measurement.Protocol));
        await onOutput($"Reported {ports.Count} listening port(s) ({measured}).", TaskLogLevel.Info).ConfigureAwait(false);
        if (scan.Measured.Count < 2)
            await onOutput(
                "One protocol could not be read: its observed ports were left unchanged.", TaskLogLevel.Warning)
                .ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }
}
