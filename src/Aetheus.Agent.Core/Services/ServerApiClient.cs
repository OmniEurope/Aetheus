// SPDX-License-Identifier: EUPL-1.2
using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aetheus.Agent.Core.Services;

public sealed class ServerApiClient(
    IHttpClientFactory httpClientFactory,
    AgentState agentState) : IServerApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public void SetBearerToken(string token)
    {
        // Token is stored in AgentState and applied by BearerTokenHandler
    }

    public async Task<ServerRegistrationResponse?> RegisterAsync(
        ServerRegistrationRequest request, CancellationToken ct = default)
    {
        return await PostAsync<ServerRegistrationResponse>("api/auth/register", request, ct).ConfigureAwait(false);
    }

    public async Task<ServerHeartbeatResponseDto?> SendHeartbeatAsync(int serverId, ServerHeartbeatDto heartbeat, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient("AetheusServer");
        using var response = await client.PostAsJsonAsync($"api/servers/{serverId}/heartbeat", heartbeat, JsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        // Tolerate an empty 200 body - e.g. a not-yet-upgraded backend that still
        // returns a bare Ok(). Empty simply means "no token renewal this beat".
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(body)
            ? null
            : JsonSerializer.Deserialize<ServerHeartbeatResponseDto>(body, JsonOptions);
    }

    public async Task<List<PendingTaskDto>> GetPendingTasksAsync(int serverId, int freeSlots, CancellationToken ct = default)
    {
        // take = free execution slots: the backend claims only what this agent can run now
        // (self-update tasks are claimed regardless - they bypass the concurrency gate).
        var session = Uri.EscapeDataString(agentState.SessionId);
        var tasks = await PostAsync<List<PendingTaskDto>>(
            $"api/tasks/claim?serverId={serverId}&take={freeSlots}&agentSessionId={session}",
            new { },
            ct).ConfigureAwait(false) ?? [];
        foreach (var task in tasks)
            agentState.TrackTaskLease(task.Id, task.AgentSessionFencingToken);
        return tasks;
    }

    public async Task StartTaskAsync(int taskId, CancellationToken ct = default)
    {
        await PostAsync($"api/tasks/{taskId}/start", agentState.GetTaskLease(taskId), ct).ConfigureAwait(false);
    }

    public async Task ReleaseTaskAsync(int taskId, CancellationToken ct = default)
    {
        await PostAsync($"api/tasks/{taskId}/release", agentState.GetTaskLease(taskId), ct).ConfigureAwait(false);
    }

    public async Task CompleteTaskAsync(int taskId, TaskResultDto result, CancellationToken ct = default)
    {
        var fencedResult = result with
        {
            AgentSessionId = agentState.SessionId,
            AgentSessionFencingToken = agentState.GetTaskFencingToken(taskId)
        };
        await PostAsync($"api/tasks/{taskId}/complete", fencedResult, ct).ConfigureAwait(false);
        agentState.ReleaseTaskLease(taskId);
    }

    public async Task ReportDeploymentBuildRefusalAsync(
        DeploymentBuildRefusalReport report,
        CancellationToken ct = default)
    {
        await PostAsync(
            $"api/tasks/{report.TaskId}/deployment-build-refusal",
            report,
            ct).ConfigureAwait(false);
    }

    public async Task ReportObservedPortsAsync(
        int serverId, ObservedPortsReportDto report, CancellationToken ct = default)
    {
        await PostAsync($"api/servers/{serverId}/ports/observed", report, ct).ConfigureAwait(false);
    }

    public async Task ReportHostPackagesAsync(
        int serverId, HostPackageInventoryReportDto report, CancellationToken ct = default)
    {
        await PostAsync($"api/servers/{serverId}/host-packages", report, ct).ConfigureAwait(false);
    }

    public async Task ReportBackupResultAsync(int runId, BackupExecuteResultDto result, CancellationToken ct = default)
    {
        await PostAsync($"api/backups/runs/{runId}/result", result, ct).ConfigureAwait(false);
    }

    public async Task ReportRestoreCheckResultAsync(int runId, RestoreCheckResultDto result, CancellationToken ct = default)
    {
        await PostAsync($"api/backups/runs/{runId}/restore-check-result", result, ct).ConfigureAwait(false);
    }

    public async Task ReportUpdateProgressAsync(int serverId, AgentUpdateProgressReport report, CancellationToken ct = default)
    {
        // Best-effort: a 404 from a backend that predates item #3, a 401 during a token
        // renewal window, or a connection refused during shutdown must NOT bubble up to
        // the self-update executor - those would abort the actual update.
        try
        {
            using var client = httpClientFactory.CreateClient("AetheusServer");
            using var response = await client.PostAsJsonAsync(
                $"api/servers/{serverId}/agent/progress", report, JsonOptions, ct).ConfigureAwait(false);
            // Intentionally do not call EnsureSuccessAsync - silent failure is correct here.
        }
        catch (Exception)
        {
            // Telemetry-grade - swallowed by design.
        }
    }

    public async Task<PipelineArtifactDto?> UploadArtifactAsync(int runId, string name, string? stageName, Stream zipContent, CancellationToken ct = default)
    {
        // Past the threshold a single request cannot carry the artifact, and the old answer was to
        // split it into four separately named artifacts reassembled by `cat` in three YAML files.
        // Parts keep it one artifact, with a digest per part and one for the whole.
        if (zipContent.CanSeek
            && zipContent.Length - zipContent.Position > ArtifactChunkPlan.ChunkThresholdBytes)
        {
            return await UploadArtifactInPartsAsync(runId, name, stageName, zipContent, ct).ConfigureAwait(false);
        }

        // Transfer client: no retry (the stream is partially consumed on failure) and a
        // timeout sized for multi-hundred-MB uploads, unlike the 30s RPC pipeline.
        using var client = httpClientFactory.CreateClient("AetheusServerTransfer");
        var query = $"api/artifacts/upload/{runId}?name={Uri.EscapeDataString(name)}";
        if (!string.IsNullOrEmpty(stageName))
            query += $"&stageName={Uri.EscapeDataString(stageName)}";

        using var content = new StreamContent(zipContent);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, query) { Content = content };
        // The backend can reject a large artifact during the quota preflight. Waiting for
        // 100 Continue avoids sending hundreds of MB only to receive that deterministic rejection,
        // and prevents reverse proxies from surfacing the early close as a misleading 502.
        request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PipelineArtifactDto>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>How many times one part is re-sent before the upload is abandoned. A part is the
    /// unit of retry precisely so a network cut does not cost the whole gigabyte.</summary>
    private const int PartAttempts = 3;

    private async Task<PipelineArtifactDto?> UploadArtifactInPartsAsync(
        int runId, string name, string? stageName, Stream content, CancellationToken ct)
    {
        var plan = await ArtifactChunkPlan.BuildAsync(content, ct).ConfigureAwait(false);
        using var client = httpClientFactory.CreateClient("AetheusServerTransfer");

        var query = $"name={Uri.EscapeDataString(name)}";
        if (!string.IsNullOrEmpty(stageName)) query += $"&stageName={Uri.EscapeDataString(stageName)}";

        using var beginResponse = await client.PostAsync(
            $"api/artifacts/upload/{runId}/begin?{query}&totalParts={plan.Chunks.Count}&sha256={plan.Sha256}",
            content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(beginResponse, ct).ConfigureAwait(false);
        var session = await beginResponse.Content
            .ReadFromJsonAsync<ChunkedUploadSessionDto>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The server accepted the upload but returned no session.");

        try
        {
            foreach (var chunk in plan.Chunks)
                await SendPartAsync(client, runId, session.UploadId, content, chunk, ct).ConfigureAwait(false);

            using var complete = await client.PostAsync(
                $"api/artifacts/upload/{runId}/complete/{session.UploadId}?{query}", content: null, ct)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(complete, ct).ConfigureAwait(false);
            return await complete.Content.ReadFromJsonAsync<PipelineArtifactDto>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch
        {
            // Abandon rather than leave a gigabyte of parts for the 24h sweeper to find. Best-effort:
            // the failure being reported is the one that matters, not this cleanup.
            try
            {
                using var _ = await client.DeleteAsync(
                    $"api/artifacts/upload/{runId}/{session.UploadId}", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanup) when (cleanup is HttpRequestException or TaskCanceledException) { } // best-effort abandon; the original failure is rethrown below
            throw;
        }
    }

    private static async Task SendPartAsync(
        HttpClient client, int runId, string uploadId, Stream content, ArtifactChunk chunk, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A fresh window per attempt: the previous one was partially consumed, which is
                // exactly why the single-request upload could not be retried at all.
                using var window = new ArtifactChunkStream(content, chunk);
                using var body = new StreamContent(window);
                body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using var response = await client.PostAsync(
                    $"api/artifacts/upload/{runId}/part/{uploadId}/{chunk.Index}?sha256={chunk.Sha256}",
                    body, ct).ConfigureAwait(false);
                await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < PartAttempts && ex is HttpRequestException or IOException)
            {
                // Only transport failures are retried. A 4xx means the server refused these bytes,
                // and re-sending the same bytes would be refused the same way.
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>What <c>begin</c> returns. Only the id is used; the rest is the server's echo of what
    /// it recorded, kept out of the agent so the two do not have to agree on more than they must.</summary>
    private sealed record ChunkedUploadSessionDto(string UploadId);

    public async Task<Stream?> DownloadArtifactAsync(int artifactId, int deployRunId, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient("AetheusServerTransfer");
        var url = $"api/artifacts/agent-download/{artifactId}?runId={deployRunId}";
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        // Buffer to a self-deleting temp file: a container image can be hundreds of MB, so we never
        // hold it in memory, and the HttpClient/response is disposed before the caller reads the bytes.
        var tempPath = Path.Combine(Path.GetTempPath(), $"prom-deploy-{artifactId}-{Guid.NewGuid():N}.zip");
        var temp = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            var download = Stopwatch.StartNew();
            var hashElapsed = TimeSpan.Zero;
            long bytes = 0;
            try
            {
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    var hashStopwatch = Stopwatch.StartNew();
                    hash.AppendData(buffer, 0, read);
                    hashStopwatch.Stop();
                    hashElapsed += hashStopwatch.Elapsed;
                    await temp.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    bytes = checked(bytes + read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            download.Stop();
            temp.Position = 0;
            ArtifactDownloadTelemetry.Register(temp, new ArtifactDownloadMeasurement(
                Convert.ToHexStringLower(hash.GetHashAndReset()),
                bytes,
                download.Elapsed,
                hashElapsed));
            return temp;
        }
        catch
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ReleaseCreatedResponse?> CreateReleaseAsync(int projectId, int pipelineRunId, string version, string? changelog,
        string? commitHash = null, string? branch = null, int? artifactPipelineRunId = null,
        bool deployed = false, CancellationToken ct = default)
    {
        var request = new CreateReleaseRequest
        {
            Version = version,
            Changelog = changelog,
            PipelineRunId = pipelineRunId,
            ArtifactPipelineRunId = artifactPipelineRunId,
            CommitHash = commitHash,
            BranchName = branch,
            Deployed = deployed
        };
        return await PostAsync<ReleaseCreatedResponse>($"api/releases/create?projectId={projectId}", request, ct).ConfigureAwait(false);
    }

    public async Task PublishCoverageAsync(int runId, string xmlContent, string? stageName, string? stepName, CancellationToken ct = default)
    {
        // Coverage can approach the 100 MiB report cap. JSON wrapping escapes XML attributes and
        // can push an otherwise valid report over that limit, so the agent uses the raw bounded
        // transfer endpoint. The legacy JSON endpoint remains available for older agents.
        var utf8Bytes = Encoding.UTF8.GetByteCount(xmlContent);
        if (utf8Bytes > CoverageUploadLimits.MaxRawXmlBytes)
        {
            throw new HttpRequestException(
                $"Coverage XML is {utf8Bytes} UTF-8 bytes; maximum is {CoverageUploadLimits.MaxRawXmlBytes}.",
                inner: null,
                statusCode: System.Net.HttpStatusCode.RequestEntityTooLarge);
        }

        var query = $"api/pipelines/runs/{runId}/coverage/raw";
        if (!string.IsNullOrWhiteSpace(stageName))
            query += $"?stageName={Uri.EscapeDataString(stageName)}";
        if (!string.IsNullOrWhiteSpace(stepName))
            query += $"{(query.Contains('?') ? '&' : '?')}stepName={Uri.EscapeDataString(stepName)}";

        using var client = httpClientFactory.CreateClient("AetheusServerTransfer");
        using var content = new StringContent(xmlContent, Encoding.UTF8, "application/xml");
        using var request = new HttpRequestMessage(HttpMethod.Post, query) { Content = content };
        request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task PublishLintAsync(int runId, string sarifContent, string? stageName, string? stepName, CancellationToken ct = default)
    {
        var request = new { SarifContent = sarifContent, StageName = stageName, StepName = stepName };
        await PostAsync($"api/pipelines/runs/{runId}/lint", request, ct).ConfigureAwait(false);
    }

    public async Task PublishComplexityAsync(int runId, double avgCyclomatic, int maxCyclomatic, int totalMethods, int highComplexityMethods, int totalLinesOfCode, string? stageName, CancellationToken ct = default)
    {
        var request = new
        {
            AvgCyclomatic = avgCyclomatic,
            MaxCyclomatic = maxCyclomatic,
            TotalMethods = totalMethods,
            HighComplexityMethods = highComplexityMethods,
            TotalLinesOfCode = totalLinesOfCode,
            StageName = stageName
        };
        await PostAsync($"api/pipelines/runs/{runId}/complexity", request, ct).ConfigureAwait(false);
    }

    public Task<AnalysisReportDto?> PublishAnalysisReportAsync(
        int runId,
        PublishAnalysisReportRequest request,
        CancellationToken ct = default) =>
        PostTransferAsync<AnalysisReportDto>($"api/analysis/runs/{runId}/reports", request, ct);

    public async Task<AnalysisRunGateDto?> GetAnalysisRunGateAsync(
        int runId,
        string scope,
        CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient("AetheusServer");
        using var response = await client.GetAsync(
            $"api/analysis/runs/{runId}/gate?scope={Uri.EscapeDataString(scope)}",
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AnalysisRunGateDto>(JsonOptions, ct).ConfigureAwait(false);
    }

    public Task<AiRunResultDto?> PublishAiRunResultAsync(
        PublishAiRunResultRequest request,
        CancellationToken ct = default) =>
        PostTransferAsync<AiRunResultDto>("api/ai/results", request, ct);

    public async Task AppendLogAsync(AppendLogRequest log, CancellationToken ct = default)
    {
        await PostAsync("api/logs", StampLogLease(log), ct).ConfigureAwait(false);
    }

    public async Task AppendLogBatchAsync(List<AppendLogRequest> logs, CancellationToken ct = default)
    {
        await PostAsync("api/logs/batch", logs.Select(StampLogLease).ToList(), ct).ConfigureAwait(false);
    }

    private AppendLogRequest StampLogLease(AppendLogRequest log) => log with
    {
        AgentSessionId = agentState.SessionId,
        AgentSessionFencingToken = agentState.GetTaskFencingToken(log.TaskId)
    };

    public async Task<Dictionary<int, string>> GetTaskStatusesAsync(List<int> taskIds, CancellationToken ct = default)
    {
        return await PostAsync<Dictionary<int, string>>("api/tasks/statuses", taskIds, ct).ConfigureAwait(false) ?? new();
    }

    public async Task<IReadOnlyList<string>?> GetActiveWorkspaceSlotsAsync(CancellationToken ct = default)
    {
        // Null on any failure: the reaper deletes nothing rather than guess that no run is alive.
        try
        {
            using var client = httpClientFactory.CreateClient("AetheusServer");
            using var response = await client.GetAsync("api/pipelines/runs/active-workspaces", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content
                .ReadFromJsonAsync<List<string>>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<List<AppProbeConfigDto>> GetAppProbesAsync(CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient("AetheusServer");
        using var response = await client.GetAsync("api/appmonitoring/agent/probes", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<AppProbeConfigDto>>(JsonOptions, ct).ConfigureAwait(false) ?? [];
    }

    public async Task ReportAppProbeResultsAsync(List<AppProbeResultDto> results, CancellationToken ct = default)
    {
        await PostAsync("api/appmonitoring/agent/probe-results", results, ct).ConfigureAwait(false);
    }

    private async Task<T?> PostAsync<T>(string url, object body, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("AetheusServer");
        using var response = await client.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
    }

    private async Task PostAsync(string url, object body, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("AetheusServer");
        using var response = await client.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<T?> PostTransferAsync<T>(string url, object body, CancellationToken ct)
    {
        // Completed scanner reports can contain tens of thousands of normalized records and the
        // endpoint accepts up to 100 MiB. Use the bounded 10-minute transfer client without retries:
        // the 30-second RPC resilience budget is intentionally reserved for small control messages.
        using var client = httpClientFactory.CreateClient("AetheusServerTransfer");
        using var response = await client.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
    }

    // Surfaces the response body in the exception message so agent logs reveal which model
    // validation rule (or auth issue) the server rejected - otherwise heartbeats fail silently
    // with just the status code.
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? body = null;
        try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { /* best-effort */ }
        var trimmed = string.IsNullOrWhiteSpace(body) ? "<empty>" : body.Length > 1024 ? body[..1024] + "…" : body;
        throw new HttpRequestException(
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} - {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: {trimmed}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
