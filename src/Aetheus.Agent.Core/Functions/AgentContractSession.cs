// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Agent.Core.Functions;

/// <summary>The inputs of one <c>verify-nminus1-agent-contract</c> call, with the script's
/// refusals.</summary>
internal sealed record AgentProof(string BackendUrl, string PasswordFile, string AgentRelease, string ExpectedCommit, string EvidencePath)
{
    public static AgentProof From(PipelineFunctionContext context) => new(
        context.Parameters["backend_url"],
        Path.GetFullPath(context.Parameters["admin_password_file"], context.Workspace!),
        Path.GetFullPath(context.Parameters["agent_release"], context.Workspace!),
        context.Parameters["expected_commit"],
        Path.GetFullPath(context.Parameters["evidence"], context.Workspace!));

    public string? Refusal()
    {
        if (!BackendUrl.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
            && !BackendUrl.StartsWith("http://localhost:", StringComparison.Ordinal))
            return "N-1 agent proof is restricted to a loopback QA backend.";
        if (ExpectedCommit.Length == 0 || !ExpectedCommit.All(char.IsAsciiHexDigit))
            return "N-1 commit must be hexadecimal.";
        if (!HasContent(PasswordFile))
            return $"The admin password file is missing or empty: {PasswordFile}";
        var manifest = Path.Combine(AgentRelease, AgentReleaseManifest.FileName);
        return HasContent(manifest) ? null : $"The N-1 agent release has no manifest: {manifest}";
    }

    private static bool HasContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
}

/// <summary>Which of the three proofs held, the ids they produced, and the finding when one did
/// not.</summary>
internal sealed record AgentContractOutcome(
    bool Enrollment, bool Claim, bool Publication, string ServerId, string TaskId, string? Failure);

/// <summary>The attestation the script's <c>write_evidence</c> wrote: two-space indented JSON, the
/// ids as numbers or null.</summary>
internal sealed record AgentEvidence(AgentProof Proof, string ArchiveName, string ArchiveSha256)
{
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };

    public async Task WriteAsync(string status, AgentContractOutcome outcome, CancellationToken cancellationToken)
    {
        var evidence = new
        {
            schema = 1,
            mode = "NMinusOne",
            status,
            previousAgentCommit = Proof.ExpectedCommit,
            archiveName = ArchiveName,
            archiveSha256 = ArchiveSha256,
            proofs = new
            {
                enrollmentAndHeartbeat = outcome.Enrollment,
                taskClaimAndStart = outcome.Claim,
                taskResultPublication = outcome.Publication
            },
            serverId = Id(outcome.ServerId),
            taskId = Id(outcome.TaskId)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Proof.EvidencePath)!);
        await File.WriteAllTextAsync(Proof.EvidencePath, JsonSerializer.Serialize(evidence, Indented) + "\n", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary><c>Number(id) || null</c>.</summary>
    private static double? Id(string id) =>
        double.TryParse(id, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value != 0 && !double.IsNaN(value)
            ? value
            : null;
}

/// <summary>
/// The backend side of the N-1 agent proof: the admin session and registration token, then the polls
/// that observe the agent enroll and run a task. A request the backend refuses throws
/// <see cref="FunctionHttpException"/> (a technical failure); a proof that does not hold within its
/// polls is an <see cref="AgentContractOutcome"/> carrying the finding.
/// </summary>
internal sealed class AgentContractSession(FunctionHttp http, string backendUrl, IPipelineFunctionTools tools)
{
    private static readonly HashSet<string> FailedTaskStatuses = new(StringComparer.Ordinal)
    {
        "4", "5", "6", "Failed", "Timeout", "Cancelled"
    };

    private string? _adminToken;

    public string RegistrationToken { get; private set; } = string.Empty;

    public string RegistrationTokenId { get; private set; } = string.Empty;

    public async Task SignInAsync(string passwordFile, string temporary, CancellationToken cancellationToken)
    {
        var password = (await File.ReadAllTextAsync(passwordFile, cancellationToken).ConfigureAwait(false)).TrimEnd('\n');
        var login = await http.PostJsonAsync(
            backendUrl + "/api/auth/login", JsonSerializer.Serialize(new { username = "admin", password }), null, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(temporary, "login.json"), login, cancellationToken).ConfigureAwait(false);
        _adminToken = Written(login, "token") ?? string.Empty;
        if (_adminToken.Length == 0)
            throw new FunctionHttpException("QA login did not return a bearer token.");

        var registration = await http.PostJsonAsync(
            backendUrl + "/api/auth/registration-tokens", "{\"expirationHours\":1}", _adminToken, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(temporary, "registration-token.json"), registration, cancellationToken).ConfigureAwait(false);
        RegistrationToken = Written(registration, "token")
            ?? throw new FunctionHttpException("The backend issued no registration token.");
        RegistrationTokenId = Text(registration, "id");
    }

    public async Task<AgentContractOutcome> ProveAsync(CancellationToken cancellationToken)
    {
        var serverId = await WaitForEnrollmentAsync(cancellationToken).ConfigureAwait(false);
        if (serverId.Length == 0)
            return new AgentContractOutcome(false, false, false, string.Empty, string.Empty,
                "The retained N-1 agent did not enroll and heartbeat against backend N.");

        var task = await http.PostJsonAsync(
            backendUrl + "/api/tasks",
            $"{{\"serverId\":{serverId},\"name\":\"N-1 protocol proof\",\"command\":\"echo NMinusOneProtocol\",\"executor\":0,\"timeoutSeconds\":60}}",
            _adminToken,
            cancellationToken).ConfigureAwait(false);
        var taskId = Text(task, "id");
        var status = await WaitForTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (status is "3" or "Success")
            return new AgentContractOutcome(true, true, true, serverId, taskId, null);
        var failure = FailedTaskStatuses.Contains(status)
            ? $"The retained N-1 agent failed its claim/start/complete proof: {status}"
            : "The retained N-1 agent did not publish a successful task result.";
        return new AgentContractOutcome(true, true, false, serverId, taskId, failure);
    }

    /// <summary>The server the registration token was used by, or empty after the last poll.</summary>
    private async Task<string> WaitForEnrollmentAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < VerifyNMinusOneAgentContractFunction.Polls; attempt++)
        {
            var token = await http.GetAsync(backendUrl + "/api/auth/registration-tokens/" + RegistrationTokenId, _adminToken, cancellationToken)
                .ConfigureAwait(false);
            if (Truthy(token, "usedByServerId") is { } serverId)
                return serverId;
            await tools.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        return string.Empty;
    }

    /// <summary>The last task status read: a success or a failure as soon as one is read, else the
    /// status of the last poll.</summary>
    private async Task<string> WaitForTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var status = string.Empty;
        for (var attempt = 0; attempt < VerifyNMinusOneAgentContractFunction.Polls; attempt++)
        {
            var task = await http.GetAsync(backendUrl + "/api/tasks/" + taskId, _adminToken, cancellationToken).ConfigureAwait(false);
            status = Text(task, "status");
            if (status is "3" or "Success" || FailedTaskStatuses.Contains(status))
                return status;
            await tools.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        return status;
    }

    /// <summary><c>String(JSON.parse(body).name)</c>; an absent property is <c>undefined</c>.</summary>
    internal static string Text(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return AgentReleaseManifest.Text(document.RootElement, name);
    }

    /// <summary><c>process.stdout.write(JSON.parse(body).name)</c>, which throws for an absent or null
    /// value: null then.</summary>
    private static string? Written(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        var value = AgentReleaseManifest.Property(document.RootElement, name);
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : AgentReleaseManifest.Text(document.RootElement, name);
    }

    /// <summary><c>x.name ? String(x.name) : ""</c>, as null when falsy.</summary>
    private static string? Truthy(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        var value = AgentReleaseManifest.Property(document.RootElement, name);
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.GetDouble() != 0 => value.GetRawText(),
            JsonValueKind.String when value.GetString()!.Length > 0 => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => null
        };
    }
}
