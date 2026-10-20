// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aetheus.Agent.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Services;

public sealed class EnrollmentService(
    IServerApiClient apiClient,
    IOptions<AetheusAgentOptions> options,
    AgentState agentState,
    IConfiguration configuration,
    ICredentialProtector credentialProtector,
    IShellRunner shell,
    TimeProvider timeProvider,
    ILogger<EnrollmentService> logger) : IEnrollmentService
{
    private readonly AetheusAgentOptions _options = options.Value;

    // Hardening (#58): serialize concurrent EnrollAsync calls so the IsEnrolled→register→persist
    // sequence is atomic. Without this, two callers can both pass the IsEnrolled check and
    // burn the registration token twice.
    private readonly SemaphoreSlim _enrollGate = new(1, 1);

    public bool IsEnrolled => agentState.IsEnrolled;

    public async Task<bool> EnrollAsync(CancellationToken ct = default)
    {
        await _enrollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EnrollCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _enrollGate.Release();
        }
    }

    private async Task<bool> EnrollCoreAsync(CancellationToken ct)
    {
        if (IsEnrolled)
        {
            apiClient.SetBearerToken(agentState.BearerToken!);
            logger.LogInformation("Agent already enrolled with Server ID {ServerId}", agentState.ServerId);
            return true;
        }

        var registrationToken = configuration["Aetheus:RegistrationToken"];
        if (string.IsNullOrEmpty(registrationToken))
        {
            // Persists enrollment beside credentials in .enrollment-state so read-only or templated
            // configuration files remain untouched.
            if (HasPriorEnrollmentMarker())
            {
                logger.LogError("Agent was previously enrolled (.enrollment-state present) but .credentials is missing or unreadable. " +
                    "Generate a new registration token in the web UI, delete .enrollment-state, and restart the agent.");
            }
            else
            {
                logger.LogError("No registration token configured. Set Aetheus:RegistrationToken in appsettings.json or environment variable Aetheus__RegistrationToken");
            }
            return false;
        }

        logger.LogInformation("Enrolling agent with server {ServerUrl}...", _options.ServerUrl);

        var dockerAvailable = await DockerProbe.IsAvailableAsync(shell, ct).ConfigureAwait(false);
        var pipelineRunnerAvailable = await PipelineRunnerProbe.IsAvailableAsync(shell, ct).ConfigureAwait(false);
        var request = new ServerRegistrationRequest
        {
            RegistrationToken = registrationToken,
            Hostname = !string.IsNullOrWhiteSpace(_options.Name) ? _options.Name : Environment.MachineName,
            OsDescription = RuntimeInformation.OSDescription,
            AgentVersion = typeof(EnrollmentService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilities = BuildRegistrationCapabilities(dockerAvailable, pipelineRunnerAvailable),
            IpAddress = GetLocalIpAddress(),
            AgentInstalledAt = InstallInfoReader.Read(_options.WorkDirectory),
            DockerAvailable = dockerAvailable,
            // Report the host workspace capability (Git); application SDKs are resolved in OCI
            // containers and Docker availability is reported separately, so the backend defaults the
            // per-server PipelineRunnerEnabled gate from reality instead of authorising every box.
            PipelineRunnerAvailable = pipelineRunnerAvailable,
            // S-DES-23: report the dev-only TLS-bypass mode at enrollment (known before first heartbeat).
            InsecureTls = _options.AllowInsecureCerts,
            // Send an application-keyed machine fingerprint so re-enrollment can revive the same
            // server record.
            MachineIdHash = MachineIdentity.ReadHash()
        };

        ServerRegistrationResponse? response;
        try
        {
            response = await apiClient.RegisterAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            logger.LogError(
                "Server {ServerUrl} returned {StatusCode} on POST /api/auth/register. " +
                "The ServerUrl likely points at the frontend instead of the backend API " +
                "(e.g. https://aetheus.example.com instead of https://aetheus-api.example.com). " +
                "Fix Aetheus:ServerUrl in appsettings.json and restart the agent.",
                _options.ServerUrl, (int)ex.StatusCode);
            return false;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Enrollment HTTP call to {ServerUrl} failed", _options.ServerUrl);
            return false;
        }

        if (response is null)
        {
            logger.LogError("Enrollment failed - server returned no response");
            return false;
        }

        agentState.ServerId = response.ServerId;
        agentState.BearerToken = response.BearerToken;
        apiClient.SetBearerToken(response.BearerToken);

        await PersistCredentialsAsync(response.ServerId, response.BearerToken, ct).ConfigureAwait(false);

        await MarkEnrolledAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Enrolled successfully. Server ID: {ServerId}", response.ServerId);
        return true;
    }

    public Task PersistRenewedTokenAsync(string bearerToken, CancellationToken ct = default) =>
        agentState.ServerId is { } sid
            ? PersistCredentialsAsync(sid, bearerToken, ct)
            : Task.CompletedTask;

    private static List<string> BuildRegistrationCapabilities(bool dockerAvailable, bool pipelineRunnerAvailable)
    {
        // pipeline.functions is published by this agent, not by the shared software list: only an agent
        // that carries the function executor may receive type: aetheus tasks. deployment.functions waits
        // for the first heartbeat, like deployment.apply, because it needs the probed deploy grant.
        var capabilities = new HashSet<string>(AgentCapabilities.SoftwareCapabilities, StringComparer.Ordinal)
        {
            AgentCapabilities.PipelineFunctions
        };
        if (!pipelineRunnerAvailable)
        {
            capabilities.Remove(AgentCapabilities.PipelineBuild);
            capabilities.Remove(AgentCapabilities.PipelineFunctions);
        }
        if (dockerAvailable)
            capabilities.Add(AgentCapabilities.DockerExecution);
        return capabilities.Order(StringComparer.Ordinal).ToList();
    }

    private async Task PersistCredentialsAsync(int serverId, string bearerToken, CancellationToken ct)
    {
        var workDir = string.IsNullOrWhiteSpace(_options.WorkDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : _options.WorkDirectory;
        var credPath = Path.Combine(workDir, ".credentials");
        var cred = new { ServerId = serverId, BearerToken = bearerToken };
        var json = JsonSerializer.Serialize(cred, new JsonSerializerOptions { WriteIndented = true });

        var plainBytes = System.Text.Encoding.UTF8.GetBytes(json);
        var protectedBytes = credentialProtector.Protect(plainBytes);

        Directory.CreateDirectory(workDir);
        // Hardening (#34): propagate the cancellation token through the file write.
        await File.WriteAllBytesAsync(credPath, protectedBytes, ct).ConfigureAwait(false);

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(credPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        else if (OperatingSystem.IsWindows())
        {
            // Hardening (#35): on Windows, restrict the credentials file ACL to SYSTEM and the
            // current account only. Removes inherited permissions so other users cannot read it.
            TryHardenWindowsCredentialsAcl(credPath);
        }

        logger.LogInformation("Credentials saved (encrypted) to {Path}", credPath);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void TryHardenWindowsCredentialsAcl(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();
            // Disable inheritance, drop existing inherited rules.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // Grant full control only to the current process owner and to SYSTEM.
            var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    currentUser,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            }
            var system = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                system,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));

            fileInfo.SetAccessControl(security);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Could not harden ACL on credentials file (insufficient privileges).");
        }
        catch (System.Security.SecurityException ex)
        {
            logger.LogWarning(ex, "Could not harden ACL on credentials file (security exception).");
        }
    }

    public async Task LoadPersistedCredentialsAsync(CancellationToken ct = default)
    {
        var workDir = string.IsNullOrWhiteSpace(_options.WorkDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : _options.WorkDirectory;
        var credPath = Path.Combine(workDir, ".credentials");
        if (!File.Exists(credPath)) return;

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(credPath, ct).ConfigureAwait(false);
            var plainBytes = credentialProtector.Unprotect(protectedBytes);
            var json = System.Text.Encoding.UTF8.GetString(plainBytes);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("ServerId", out var serverIdProp) &&
                root.TryGetProperty("BearerToken", out var tokenProp))
            {
                agentState.ServerId = serverIdProp.GetInt32();
                agentState.BearerToken = tokenProp.GetString();
                apiClient.SetBearerToken(agentState.BearerToken!);
                logger.LogInformation("Loaded persisted credentials for Server ID {ServerId}", agentState.ServerId);
            }
        }
        catch (CryptographicException ex)
        {
            // Hardening (#45): DPAPI/file unprotect failure means a tamper or a key-domain change
            // (e.g. machine re-image). Quarantine the file so the next run re-enrolls cleanly
            // instead of silently looping with no credentials.
            logger.LogError(ex, "Credentials file at {Path} is corrupt or unprotectable; quarantining.", credPath);
            QuarantineCredentialFile(credPath);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Credentials file at {Path} is not valid JSON; quarantining.", credPath);
            QuarantineCredentialFile(credPath);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to read persisted credentials from {Path}", credPath);
        }
    }

    private void QuarantineCredentialFile(string credPath)
    {
        try
        {
            var quarantine = credPath + ".corrupt-" + timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmss");
            File.Move(credPath, quarantine);

            // Hardening (#45): File.Move usually preserves the source ACL, but that is not
            // guaranteed after a key-domain change. Re-apply the same owner-only hardening the
            // live .credentials file gets so the quarantined (still-encrypted) blob is never
            // world-readable.
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(quarantine,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            else if (OperatingSystem.IsWindows())
            {
                TryHardenWindowsCredentialsAcl(quarantine);
            }

            logger.LogWarning("Moved corrupt credentials to {Path}; agent will re-enroll on next start.", quarantine);
        }
        catch (IOException moveEx)
        {
            logger.LogError(moveEx, "Could not quarantine corrupt credentials at {Path}", credPath);
        }
    }

    // F-31: persist enrollment marker in a sidecar file rather than mutating appsettings.json.
    // - The agent binary directory may be read-only (deb/rpm install).
    // - appsettings.json may be templated by the deployment pipeline.
    // - Marker lives next to .credentials so they share the same lifecycle.
    private string GetEnrollmentMarkerPath()
    {
        var workDir = string.IsNullOrWhiteSpace(_options.WorkDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : _options.WorkDirectory;
        return Path.Combine(workDir, ".enrollment-state");
    }

    private bool HasPriorEnrollmentMarker()
    {
        var markerPath = GetEnrollmentMarkerPath();
        if (File.Exists(markerPath)) return true;
        // Backwards-compat: legacy installs may still carry Aetheus:Enrolled=true in appsettings.
        var legacyFlag = configuration["Aetheus:Enrolled"];
        return string.Equals(legacyFlag, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task MarkEnrolledAsync(CancellationToken ct)
    {
        var markerPath = GetEnrollmentMarkerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);

        var payload = JsonSerializer.Serialize(new
        {
            EnrolledAt = timeProvider.GetUtcNow().UtcDateTime,
            agentState.ServerId
        }, new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(markerPath, payload, ct).ConfigureAwait(false);

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(markerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Best-effort removal of the consumed registration token from appsettings.json prevents its
        // reuse.
        ClearRegistrationTokenInConfig();

        logger.LogInformation("Wrote enrollment marker to {Path}", markerPath);
    }

    private void ClearRegistrationTokenInConfig() =>
        ClearRegistrationTokenInConfig(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), logger);

    /// <summary>Blanks a non-empty <c>Aetheus:RegistrationToken</c> in the given settings file, atomically;
    /// a file of any other shape is left as it was.</summary>
    internal static void ClearRegistrationTokenInConfig(string appSettingsPath, ILogger logger)
    {
        if (!File.Exists(appSettingsPath)) return;

        try
        {
            var json = File.ReadAllText(appSettingsPath);
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            if (node is not JsonObject root) return;
            if (root["Aetheus"] is not JsonObject aetheus) return;
            if (!aetheus.ContainsKey("RegistrationToken")) return;
            if (aetheus["RegistrationToken"] is not JsonValue token || !token.TryGetValue<string>(out var value) || value.Length == 0) return;

            aetheus["RegistrationToken"] = "";

            // Hardening (#45): write to a temp file then atomically replace, so a crash mid-write
            // can never truncate/corrupt appsettings.json.
            var tempPath = appSettingsPath + ".tmp";
            File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, appSettingsPath, overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not scrub registration token from appsettings.json (read-only?)");
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Could not scrub registration token from appsettings.json (no permission)");
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "appsettings.json was not valid JSON; skipping token scrub");
        }
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "unknown";
        }
        // F-42: catch the precise socket-related failure modes; never swallow generic Exception.
        catch (System.Net.Sockets.SocketException) { return "unknown"; }
        catch (ObjectDisposedException) { return "unknown"; }
        catch (PlatformNotSupportedException) { return "unknown"; }
    }
}
