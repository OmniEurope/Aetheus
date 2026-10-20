// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Services;

public interface IServerApiClient
{
    void SetBearerToken(string token);
    Task<ServerRegistrationResponse?> RegisterAsync(ServerRegistrationRequest request, CancellationToken ct = default);
    Task<ServerHeartbeatResponseDto?> SendHeartbeatAsync(int serverId, ServerHeartbeatDto heartbeat, CancellationToken ct = default);
    Task<List<PendingTaskDto>> GetPendingTasksAsync(int serverId, int freeSlots, CancellationToken ct = default);
    Task StartTaskAsync(int taskId, CancellationToken ct = default);
    Task ReleaseTaskAsync(int taskId, CancellationToken ct = default);
    Task CompleteTaskAsync(int taskId, TaskResultDto result, CancellationToken ct = default);
    Task ReportDeploymentBuildRefusalAsync(
        DeploymentBuildRefusalReport report,
        CancellationToken ct = default);

    // ADR-024 4.3: backup result callbacks.
    /// <summary>Pushes what a scan saw. Replaces the whole observed source of that server, so an
    /// empty list is a real "nothing is listening", never a partial report.</summary>
    Task ReportObservedPortsAsync(int serverId, ObservedPortsReportDto report, CancellationToken ct = default);

    Task ReportBackupResultAsync(int runId, BackupExecuteResultDto result, CancellationToken ct = default);
    Task ReportRestoreCheckResultAsync(int runId, RestoreCheckResultDto result, CancellationToken ct = default);

    Task AppendLogAsync(AppendLogRequest log, CancellationToken ct = default);
    Task AppendLogBatchAsync(List<AppendLogRequest> logs, CancellationToken ct = default);
    Task<Dictionary<int, string>> GetTaskStatusesAsync(List<int> taskIds, CancellationToken ct = default);

    /// <summary>Pulls this server's app-availability probe configs (ADR-021). ServerId is taken from the token claim.</summary>
    Task<List<AppProbeConfigDto>> GetAppProbesAsync(CancellationToken ct = default);

    /// <summary>
    /// Workspace slots of the runs still in flight. Returns null when the backend could not be
    /// reached, which the caller must not confuse with "no run is active": one deletes nothing,
    /// the other would delete every workspace on the host.
    /// </summary>
    Task<IReadOnlyList<string>?> GetActiveWorkspaceSlotsAsync(CancellationToken ct = default);

    /// <summary>Reports a batch of app-availability probe results. Best-effort; caller re-queues on failure.</summary>
    Task ReportAppProbeResultsAsync(List<AppProbeResultDto> results, CancellationToken ct = default);

    /// <summary>
    /// Reports a self-update phase to the backend (item #3 of the plan). Best-effort:
    /// a backend that doesn't yet implement the endpoint (404), a transient network
    /// hiccup, or a teardown-time send must NEVER block the actual update - the
    /// caller (<c>AgentSelfUpdateOperationExecutor</c>) swallows exceptions.
    /// </summary>
    Task ReportUpdateProgressAsync(int serverId, AgentUpdateProgressReport report, CancellationToken ct = default);

    Task<PipelineArtifactDto?> UploadArtifactAsync(int runId, string name, string? stageName, Stream zipContent, CancellationToken ct = default);

    /// <summary>Cross-agent deploy: download a build artifact zip from the backend's IDOR-safe
    /// agent endpoint. <paramref name="deployRunId"/> is the deploy run this agent is executing (used
    /// for the assignment + org authorisation check). Returns null on a non-success response.</summary>
    Task<Stream?> DownloadArtifactAsync(int artifactId, int deployRunId, CancellationToken ct = default);
    Task<ReleaseCreatedResponse?> CreateReleaseAsync(int projectId, int pipelineRunId, string version, string? changelog,
        string? commitHash = null, string? branch = null, int? artifactPipelineRunId = null,
        bool deployed = false, CancellationToken ct = default);
    Task PublishCoverageAsync(int runId, string xmlContent, string? stageName, string? stepName, CancellationToken ct = default);
    Task PublishLintAsync(int runId, string sarifContent, string? stageName, string? stepName, CancellationToken ct = default);
    Task PublishComplexityAsync(int runId, double avgCyclomatic, int maxCyclomatic, int totalMethods, int highComplexityMethods, int totalLinesOfCode, string? stageName, CancellationToken ct = default);
    Task<AnalysisReportDto?> PublishAnalysisReportAsync(int runId, PublishAnalysisReportRequest request, CancellationToken ct = default);
    Task<AnalysisRunGateDto?> GetAnalysisRunGateAsync(
        int runId, string scope, CancellationToken ct = default);
    Task<AiRunResultDto?> PublishAiRunResultAsync(
        PublishAiRunResultRequest request, CancellationToken ct = default);
}
