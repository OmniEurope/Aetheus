// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aetheus.Agent.Core.Toolchains;

namespace Aetheus.Agent.Core.Executors;

/// <summary>
/// Phase 2 isolation: runs a step's shell script inside a fresh, hardened, single-use container
/// (<c>docker run --rm</c>). The boundary here is the container, not a command allow-list - so the
/// script is delivered verbatim, but the container drops every Linux capability, forbids privilege
/// escalation, caps its process count, and is destroyed as soon as the step finishes.
/// <para>
/// The run's source lives in a per-run host directory (<c>{WorkDir}/cw/{runId}</c>) bind-mounted at
/// <c>/w</c>, so every step of a run shares the cloned tree while staying isolated from other runs.
/// The script is written into that mounted directory (no shell-quoting of the user script) and run
/// as <c>/w/.prom-step-*.sh</c>. The container runtime is pluggable (<c>runc</c> default, <c>runsc</c>
/// for gVisor, <c>kata</c> for micro-VMs) so kernel-level isolation can be switched on without code.
/// </para>
/// </summary>
public sealed class ContainerRunExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<ContainerRunExecutor> logger,
    IToolchainResolver toolchainResolver,
    TimeProvider timeProvider) : IContainerExecutor
{
    private static readonly TimeSpan OutputPublicationTimeout = TimeSpan.FromSeconds(60);
    internal const string HostWorkspaceVariable = "AETHEUS_HOST_WORKSPACE";
    private readonly AetheusAgentOptions _options = options.Value;
    private readonly IToolchainResolver _toolchainResolver = toolchainResolver;

    private const int PidsLimit = 4096;
    private const int WindowsContainerUid = 65532;
    private const int WindowsContainerGid = 65532;

    // Closed allow-list of OCI runtimes that may reach `docker run --runtime`. `runc` is the
    // implicit default (never passed explicitly); only the kernel-isolating runtimes are accepted.
    // Defense-in-depth: docker already rejects an unregistered runtime, but a verbatim arbitrary
    // value would otherwise undermine the isolation guarantee this executor exists to provide.
    private static readonly HashSet<string> AllowedRuntimes =
        new(StringComparer.OrdinalIgnoreCase) { "runc", "runsc", "kata" };

    public async Task<ExecutorResult> ExecuteAsync(
        ContainerSpec spec,
        string command,
        Dictionary<string, string> environmentVariables,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var runtimeFailure = await ValidateRuntimeAsync(spec.Runtime, onOutput).ConfigureAwait(false);
        if (runtimeFailure is not null) return runtimeFailure;

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        // Stage-level container isolation reuses the source tree cloned by the host System:Prepare
        // task. Run-level isolation has no host checkout and therefore keeps the agent-private
        // fallback. Never accept an arbitrary path from the task environment: only the exact
        // deterministic path derived from WorkspaceKey is eligible.
        var preparedWorkspace = ResolveHostWorkspace(spec, environmentVariables);
        var fallbackWorkspace = Path.Combine(_options.WorkDirectory, "cw", spec.WorkspaceKey.ToString());
        var materialized = await ContainerWorkspaceMaterializer.MaterializeAsync(
            spec.WorkspaceKey,
            preparedWorkspace,
            fallbackWorkspace,
            logger,
            cancellationToken).ConfigureAwait(false);
        if (!materialized.IsSuccess)
        {
            await onOutput($"InfrastructureMismatch: {materialized.FailureReason}", TaskLogLevel.Error)
                .ConfigureAwait(false);
            return new ExecutorResult(
                ToolchainResolution.InfrastructureMismatchExitCode,
                false,
                ToolchainResolution.InfrastructureMismatchCode,
                materialized.FailureReason);
        }

        var hostWorkspace = materialized.Workspace;
        Directory.CreateDirectory(hostWorkspace);
        TrySetOwnerOnly(hostWorkspace, "workspace");

        var resolution = await _toolchainResolver.ResolveAsync(spec, hostWorkspace, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.IsSuccess)
        {
            var reason = resolution.FailureReason ?? "The container execution contract is invalid.";
            await onOutput($"InfrastructureMismatch: {reason}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(
                ToolchainResolution.InfrastructureMismatchExitCode,
                false,
                ToolchainResolution.InfrastructureMismatchCode,
                reason);
        }

        var stateRoot = Path.Combine(_options.WorkDirectory, "container-state", spec.WorkspaceKey.ToString());
        var homePath = Path.Combine(stateRoot, "home");
        Directory.CreateDirectory(homePath);
        TrySetOwnerOnly(stateRoot, "container state");
        TrySetOwnerOnly(homePath, "container home");
        var cacheMounts = ToolchainCacheManager.CreateMounts(
            _options.WorkDirectory, spec, resolution, homePath, logger, timeProvider);

        var scriptName = CreateScriptName();
        var scriptPath = Path.Combine(hostWorkspace, scriptName);
        var preflight = resolution.BuildPreflightScript();
        var effectiveCommand = string.IsNullOrEmpty(preflight)
            ? command
            : $"{preflight}\n{command}";
        await File.WriteAllTextAsync(scriptPath, effectiveCommand, cancellationToken).ConfigureAwait(false);

        var containerName = $"prom-{Guid.NewGuid():N}";
        // Decide the secret file paths SYNCHRONOUSLY (no I/O) so they are known before the try/finally.
        // The actual writes happen INSIDE the try below, so a mid-write cancellation leaves the partial
        // secret-bearing files to the finally's cleanup rather than leaking them.
        // Single-line values -> owner-only --env-file. S-TECH-Q9MF: multi-line values (e.g. PEM keys) that
        // the env-file format cannot represent go through an owner-only shell file bind-mounted read-only
        // and sourced by the container - NOT the former inline `-e KEY=VALUE`, which stays visible in
        // /proc/<pid>/cmdline for the duration of the run.
        var secrets = ClassifySecrets(environmentVariables);
        if (secrets.InvalidKeys.Count > 0)
        {
            foreach (var key in secrets.InvalidKeys)
                await onOutput(
                    $"Container environment variable '{key}' cannot be delivered securely: multiline values require a valid POSIX identifier.",
                    TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var (envFilePath, secretsFilePath) = PrepareSecretFilePaths(stateRoot, containerName, secrets);
        var psi = BuildDockerRun(
            spec,
            resolution,
            hostWorkspace,
            homePath,
            cacheMounts,
            scriptName,
            containerName,
            envFilePath,
            secretsFilePath,
            ResolveHostDockerInternalAddress());

        logger.LogDebug(
            "Container run: image={Image} toolchain={Toolchain} runtime={Runtime} network={Network} (timeout={Timeout}s)",
            resolution.Image,
            resolution.Toolchain ?? "direct",
            spec.Runtime ?? "runc",
            NetworkMode(spec.Network),
            timeoutSeconds);
        await onOutput(resolution.BuildProvenanceLog(), TaskLogLevel.Info).ConfigureAwait(false);

        var executionResult = await RunContainerAsync(
            psi, resolution, secrets, containerName, scriptPath, envFilePath, secretsFilePath,
            timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);

        using var publicationCts = new CancellationTokenSource(OutputPublicationTimeout);
        var synchronized = await ContainerWorkspaceMaterializer.SynchronizeBackAsync(
            spec.WorkspaceKey,
            hostWorkspace,
            preparedWorkspace,
            publicationCts.Token).ConfigureAwait(false);
        if (!synchronized.IsSuccess)
        {
            var reason = synchronized.FailureReason
                         ?? "Container outputs could not be published to the pipeline workspace.";
            await onOutput($"InfrastructureMismatch: {reason}", TaskLogLevel.Error)
                .ConfigureAwait(false);
            executionResult = new ExecutorResult(
                ToolchainResolution.InfrastructureMismatchExitCode,
                false,
                ToolchainResolution.InfrastructureMismatchCode,
                reason);
        }

        // Run cleanup step: remove the per-run daemon-visible workspace. The in-container `rm -rf /w`
        // only empties the bind mount - the host mount-point dir would otherwise leak per run.
        if (spec.PurgeWorkspace)
            PurgeWorkspace(hostWorkspace, stateRoot);

        return executionResult;
    }

    /// <summary>
    /// Secret files live under the agent's owner-only container state, never under the temp
    /// directory: the systemd unit runs with PrivateTmp=true, so a file in the service's /tmp is
    /// invisible to the host Docker daemon that bind-mounts the multi-line secrets file. Both files
    /// are created 0600 and deleted when the step ends, whatever its outcome.
    /// </summary>
    private (string? EnvFilePath, string? SecretsFilePath) PrepareSecretFilePaths(
        string stateRoot, string containerName, ContainerSecrets secrets)
    {
        var paths = ResolveSecretFilePaths(
            stateRoot, containerName, secrets.Environment.Count > 0, secrets.Mounted.Count > 0);
        if ((paths.EnvFilePath ?? paths.SecretsFilePath) is { } anyPath)
        {
            var directory = Path.GetDirectoryName(anyPath)!;
            Directory.CreateDirectory(directory);
            TrySetOwnerOnly(directory, "container secrets");
        }
        return paths;
    }

    internal static (string? EnvFilePath, string? SecretsFilePath) ResolveSecretFilePaths(
        string stateRoot, string containerName, bool hasEnvironment, bool hasMounted)
    {
        var directory = Path.Combine(stateRoot, "secrets");
        return (
            hasEnvironment ? Path.Combine(directory, $"{containerName}.env") : null,
            hasMounted ? Path.Combine(directory, $"{containerName}.secrets.sh") : null);
    }

    private static async Task<ExecutorResult?> ValidateRuntimeAsync(
        string? runtime, Func<string, TaskLogLevel, Task> onOutput)
    {
        if (string.IsNullOrWhiteSpace(runtime) || AllowedRuntimes.Contains(runtime)) return null;
        await onOutput(
            $"Container runtime '{runtime}' is not allowed (expected one of: runc, runsc, kata) - blocked.",
            TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(-1, false);
    }

    private static ContainerSecrets ClassifySecrets(Dictionary<string, string> environmentVariables)
    {
        var environment = environmentVariables
            .Where(kv => IsEnvFileRepresentable(kv.Key, kv.Value)).ToList();
        var mounted = environmentVariables
            .Where(kv => !IsEnvFileRepresentable(kv.Key, kv.Value) && IsValidShellKey(kv.Key)).ToList();
        var invalidKeys = environmentVariables
            .Where(kv => !IsEnvFileRepresentable(kv.Key, kv.Value) && !IsValidShellKey(kv.Key))
            .Select(kv => kv.Key)
            .ToList();
        return new ContainerSecrets(environment, mounted, invalidKeys);
    }

    private async Task<ExecutorResult> RunContainerAsync(
        ProcessStartInfo startInfo,
        ToolchainResolution resolution,
        ContainerSecrets secrets,
        string containerName,
        string scriptPath,
        string? envFilePath,
        string? secretsFilePath,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            if (envFilePath is not null)
                await WriteEnvFileLinesAsync(secrets.Environment, envFilePath, cancellationToken).ConfigureAwait(false);
            if (secretsFilePath is not null)
                await WriteSecretsFileLinesAsync(secrets.Mounted, secretsFilePath, cancellationToken).ConfigureAwait(false);
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            try
            {
                await Task.WhenAll(
                    ExecutorHelper.StreamOutputAsync(process.StandardOutput, TaskLogLevel.Info, onOutput, timeoutCts.Token, logger),
                    ExecutorHelper.StreamOutputAsync(process.StandardError, TaskLogLevel.Error, onOutput, timeoutCts.Token, logger))
                    .ConfigureAwait(false);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return BuildProcessResult(process.ExitCode, resolution);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Container step timed out after {Timeout}s - removing container {Name}", timeoutSeconds, containerName);
                await ForceRemoveContainerAsync(containerName).ConfigureAwait(false);
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { logger.LogDebug(ex, "Kill after timeout failed"); }
                return new ExecutorResult(-1, true);
            }
        }
        finally
        {
            await CleanupRunFilesAsync(
                containerName, scriptPath, envFilePath, secretsFilePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ExecutorResult BuildProcessResult(int exitCode, ToolchainResolution resolution) =>
        exitCode == ToolchainResolution.InfrastructureMismatchExitCode
            ? new ExecutorResult(
                exitCode,
                false,
                ToolchainResolution.InfrastructureMismatchCode,
                $"Toolchain '{resolution.Toolchain}' did not report the locked version '{resolution.Version}'.")
            : new ExecutorResult(exitCode, false);

    private sealed record ContainerSecrets(
        List<KeyValuePair<string, string>> Environment,
        List<KeyValuePair<string, string>> Mounted,
        List<string> InvalidKeys);

    private async Task CleanupRunFilesAsync(
        string containerName,
        string scriptPath,
        string? envFilePath,
        string? secretsFilePath,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            await ForceRemoveContainerAsync(containerName).ConfigureAwait(false);
        try { File.Delete(scriptPath); } catch (Exception ex) { logger.LogDebug(ex, "Step script cleanup failed"); }
        if (envFilePath is not null)
        {
            try { File.Delete(envFilePath); } catch (Exception ex) { logger.LogDebug(ex, "Env file cleanup failed"); }
        }
        if (secretsFilePath is not null)
        {
            try { File.Delete(secretsFilePath); } catch (Exception ex) { logger.LogDebug(ex, "Secrets file cleanup failed"); }
        }
    }

    private void PurgeWorkspace(string hostWorkspace, string stateRoot)
    {
        try
        {
            if (Directory.Exists(hostWorkspace))
                Directory.Delete(hostWorkspace, recursive: true);
            if (Directory.Exists(stateRoot))
                Directory.Delete(stateRoot, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Workspace purge failed for {Workspace}", hostWorkspace);
        }
    }

    // Fixed in-container path the multi-line-secret shell file is bind-mounted at and sourced from.
    private const string ContainerSecretsPath = "/run/aetheus/.step-secrets.sh";

    internal ProcessStartInfo BuildDockerRun(
        ContainerSpec spec,
        ToolchainResolution resolution,
        string hostWorkspace,
        string homePath,
        IReadOnlyList<KeyValuePair<string, string>> cacheMounts,
        string scriptName,
        string containerName,
        string? envFilePath,
        string? secretsFilePath,
        string? hostDockerInternalAddress = null)
    {
        if (!IsShellSafeScriptName(scriptName))
            throw new ArgumentException("Container step script name is not a shell-safe token.", nameof(scriptName));

        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var a = psi.ArgumentList;
        a.Add("run");
        a.Add("--rm");
        a.Add("--name"); a.Add(containerName);
        // Hardening: no Linux capabilities, no privilege escalation, bounded process count.
        a.Add("--cap-drop"); a.Add("ALL");
        a.Add("--security-opt"); a.Add("no-new-privileges");
        a.Add("--pids-limit"); a.Add(PidsLimit.ToString());
        a.Add("--read-only");
        a.Add("--tmpfs"); a.Add("/tmp:rw,nosuid,nodev,noexec,size=512m");
        AddResourceLimits(a, spec);
        // S-TECH-72: always run application toolchains as a non-root UID/GID. Linux agents reuse
        // their own identity so workspace outputs remain host-accessible. Docker Desktop receives
        // an arbitrary fixed identity and writable tmpfs state because Windows bind mounts cannot
        // reliably preserve POSIX ownership for HOME/cache directories.
        var containerUid = OperatingSystem.IsWindows() ? WindowsContainerUid : AgentUserIdentity.Uid;
        var containerGid = OperatingSystem.IsWindows() ? WindowsContainerGid : AgentUserIdentity.Gid;
        a.Add("--user"); a.Add($"{containerUid}:{containerGid}");
        var networkMode = NetworkMode(spec.Network);
        a.Add("--network"); a.Add(networkMode);
        // A nested runner (the local VPS simulator is Docker-in-Docker) does not inherit the
        // outer host.docker.internal mapping. Preserve the authority used by the agent itself by
        // forwarding its resolved IPv4 address into network-enabled toolchain containers.
        if (networkMode == "bridge" && !string.IsNullOrWhiteSpace(hostDockerInternalAddress))
        {
            a.Add("--add-host");
            a.Add($"host.docker.internal:{hostDockerInternalAddress}");
        }
        if (networkMode == "bridge")
        {
            // Keep the runner's own Docker host address distinct from the outer host authority
            // above. Toolchain containers use this alias for run-scoped services (QA, preview,
            // integration targets) published by the runner's daemon.
            a.Add("--add-host");
            a.Add("runner.host.internal:host-gateway");
        }
        if (!string.IsNullOrWhiteSpace(spec.Runtime) && !string.Equals(spec.Runtime, "runc", StringComparison.OrdinalIgnoreCase))
        {
            a.Add("--runtime"); a.Add(spec.Runtime);
        }
        // Secrets stay off the docker CLI's argv (/proc/<pid>/cmdline is world-readable while the client
        // runs): single-line values travel via the owner-only env-file; multi-line values (S-TECH-Q9MF)
        // via an owner-only shell file bind-mounted read-only and sourced by the container. Neither ever
        // appears on argv.
        if (envFilePath is not null)
        {
            a.Add("--env-file"); a.Add(envFilePath);
        }
        if (secretsFilePath is not null)
        {
            a.Add("-v"); a.Add($"{secretsFilePath}:{ContainerSecretsPath}:ro");
        }
        a.Add("-v"); a.Add($"{hostWorkspace}:/w");
        if (OperatingSystem.IsWindows())
        {
            a.Add("--tmpfs");
            a.Add(
                $"/home/aetheus:rw,nosuid,nodev,uid={containerUid},gid={containerGid},mode=0700,size=512m");
        }
        else
        {
            a.Add("-v"); a.Add($"{homePath}:/home/aetheus");
            foreach (var cache in cacheMounts)
            {
                a.Add("-v");
                a.Add($"{cache.Key}:{cache.Value}");
            }
        }
        a.Add("--env"); a.Add("HOME=/home/aetheus");
        a.Add("--env"); a.Add("DOTNET_CLI_HOME=/home/aetheus");
        a.Add("-w"); a.Add("/w");
        // Override the image's entrypoint so the step script runs regardless of the image default.
        a.Add("--entrypoint"); a.Add(resolution.Shell);
        // End-of-options marker: everything after is positional (image, then command). Without it an
        // image reference starting with '-' (e.g. "--privileged") would be parsed by docker as a flag,
        // bypassing the hardening above. The script path is a fixed, server-controlled value.
        a.Add("--");
        a.Add(resolution.Image);
        if (secretsFilePath is not null)
        {
            // Source the mounted secrets (exports the multi-line vars), then exec the step script so the
            // secrets propagate into it. The secrets path is a fixed constant; scriptName is agent-generated
            // and MUST stay a shell-safe token (see scriptName = $".prom-step-{Guid.NewGuid():N}.sh" above -
            // alphanumeric only): it is interpolated unquoted into this -c command, so any format change that
            // introduced a space/metacharacter would break the invariant.
            a.Add("-c"); a.Add($". {ContainerSecretsPath}; exec {resolution.Shell} /w/{scriptName}");
        }
        else
        {
            a.Add($"/w/{scriptName}");
        }
        return psi;
    }

    private static void AddResourceLimits(ICollection<string> arguments, ContainerSpec spec)
    {
        // S-UX-35: optional per-run resource ceilings (validated server-side before reaching here).
        if (!string.IsNullOrWhiteSpace(spec.Memory)) { arguments.Add("--memory"); arguments.Add(spec.Memory); }
        if (!string.IsNullOrWhiteSpace(spec.Cpus)) { arguments.Add("--cpus"); arguments.Add(spec.Cpus); }
    }

    internal static string CreateScriptName() => $".prom-step-{Guid.NewGuid():N}.sh";

    internal static bool IsShellSafeScriptName(string scriptName) =>
        scriptName.StartsWith(".prom-step-", StringComparison.Ordinal)
        && scriptName.EndsWith(".sh", StringComparison.Ordinal)
        && scriptName.Length == 46
        && scriptName.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-');

    internal string ResolveHostWorkspace(
        ContainerSpec spec,
        IReadOnlyDictionary<string, string> environmentVariables)
        => HostWorkspaceResolver.Resolve(
            spec.WorkspaceKey,
            environmentVariables.GetValueOrDefault(HostWorkspaceVariable),
            _options.WorkDirectory);

    // The docker env-file format is line-based: multi-line values (e.g. PEM secrets) cannot be
    // represented and a key containing '=' or a newline would corrupt neighbouring entries.
    private static bool IsEnvFileRepresentable(string key, string value) =>
        !key.Contains('=') && !key.Contains('\n') && !key.Contains('\r')
        && !value.Contains('\n') && !value.Contains('\r');

    // A POSIX shell variable name (so it can be `export`ed from the sourced secrets file): a letter or
    // underscore followed by letters, digits, or underscores. Guards against injection via the key.
    internal static bool IsValidShellKey(string key) =>
        key.Length > 0
        && (char.IsAsciiLetter(key[0]) || key[0] == '_')
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    // Writes the multi-line secrets to an owner-only (0600) shell file as `export KEY='value'` lines,
    // single-quote-escaped so any character in the value (newlines, quotes) survives verbatim. The caller
    // owns the path decision + the finally cleanup. The file is bind-mounted read-only and sourced.
    internal static async Task WriteSecretsFileLinesAsync(
        IReadOnlyList<KeyValuePair<string, string>> secrets, string path, CancellationToken ct)
    {
        await using var stream = CreateOwnerOnlyFileStream(path);
        await using var writer = new StreamWriter(stream);
        foreach (var (key, value) in secrets)
        {
            var escaped = value.Replace("'", "'\\''", StringComparison.Ordinal);
            await writer.WriteLineAsync($"export {key}='{escaped}'".AsMemory(), ct).ConfigureAwait(false);
        }
    }

    // Writes the representable env vars to an owner-only (0600) env-file at the given path. The
    // caller owns the path decision + the finally cleanup, so a throw here leaves a partial file
    // to be deleted rather than leaked.
    private static async Task WriteEnvFileLinesAsync(
        IReadOnlyList<KeyValuePair<string, string>> representable, string path, CancellationToken ct)
    {
        await using var stream = CreateOwnerOnlyFileStream(path);
        await using var writer = new StreamWriter(stream);
        foreach (var (key, value) in representable)
            await writer.WriteLineAsync($"{key}={value}".AsMemory(), ct).ConfigureAwait(false);
    }

    private static FileStream CreateOwnerOnlyFileStream(string path)
    {
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, streamOptions);
    }

    // Default to a network-enabled container (clone/restore need it); "none" only when explicitly set.
    private static string NetworkMode(string? network)
        => string.Equals(network, "none", StringComparison.OrdinalIgnoreCase) ? "none" : "bridge";

    private static string? ResolveHostDockerInternalAddress()
    {
        try
        {
            return Dns.GetHostAddresses("host.docker.internal")
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                ?.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private void TrySetOwnerOnly(string directory, string purpose) =>
        OwnerOnlyDirectory.TrySet(directory, (exception, failedDirectory) =>
            logger.LogWarning(
                exception,
                "Could not restrict {Purpose} directory {Directory} to owner-only permissions",
                purpose,
                failedDirectory));

    private async Task ForceRemoveContainerAsync(string containerName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("rm");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(containerName);
            using var p = Process.Start(psi)!;
            // Bound the cleanup wait: a wedged daemon must not hang the step's finally block forever.
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await p.WaitForExitAsync(cleanupCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Force-remove of container {Name} failed (likely already gone)", containerName);
        }
    }
}
