// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// F-32: typed Apache operations. Single fixed command per kind, no user-controlled input.
/// Linux only - Apache on Windows is not supported by Aetheus.
/// </summary>
public sealed class ApacheOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<ApacheOperationExecutor> logger) : IOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    internal Func<string, bool> LogFileExists { get; init; } = File.Exists;
    internal Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, ILogger, CancellationToken, Task<ExecutorResult>>
        RunProcessWithLoggerAsync
    { get; init; } = static (startInfo, timeoutSeconds, onOutput, processLogger, ct) =>
            ProcessRunner.RunAsync(startInfo, timeoutSeconds, onOutput, processLogger, ct);
    internal Func<string, bool> HtaccessFileExists { get; init; } = File.Exists;
    internal Func<string, CancellationToken, Task<string>> ReadHtaccessAsync { get; init; } = File.ReadAllTextAsync;
    internal Func<string, byte[], CancellationToken, Task> WriteHtaccessAsync { get; init; } = File.WriteAllBytesAsync;
    internal string SitesAvailablePath { get; init; } = "/etc/apache2/sites-available";
    internal string SitesEnabledPath { get; init; } = "/etc/apache2/sites-enabled";
    internal Func<string, string, FileSystemInfo> CreateSiteSymbolicLink { get; init; } = File.CreateSymbolicLink;
    internal Func<OperationKind, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>>?
        PrivilegedOperationRunner
    { get; init; }

    public bool CanHandle(OperationKind kind) => kind is
        OperationKind.ApacheReload or
        OperationKind.ApacheTestConfig or
        OperationKind.ApacheGetLogs or
        OperationKind.ApacheStart or
        OperationKind.ApacheStop or
        OperationKind.ApacheRestart or
        OperationKind.ApacheSaveConfig or
        OperationKind.ApacheConfigureProxy or
        OperationKind.ApacheApplyConfigSet or
        OperationKind.ApacheGetConfig or
        OperationKind.ApacheGetHtaccess or
        OperationKind.ApacheSaveHtaccess or
        OperationKind.ApacheEnableSite or
        OperationKind.ApacheDisableSite or
        OperationKind.ApacheDeleteSite or
        OperationKind.ApacheEnableModule or
        OperationKind.ApacheDisableModule;

    /// <summary>R-119: the root-owned helper that enables or disables a module (a2enmod/a2dismod), checks the
    /// configuration and restarts Apache, undoing the change when either fails. Rendered by
    /// <c>install-agent-linux.sh --enable-apache-manage</c> from <c>deploy/agent-host-config/apache/aetheus-apache-module</c>.</summary>
    internal const string ModuleHelperPath = "/usr/local/lib/aetheus/aetheus-apache-module";

    /// <summary>Runs the module helper through sudo with the module name on stdin; replaceable so the argv and the
    /// input are asserted without sudo.</summary>
    internal Func<IReadOnlyList<string>, string, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>>?
        RunModuleHelperAsync
    { get; init; }

    private Task<ExecutorResult> RunModuleHelperWithSudoAsync(
        IReadOnlyList<string> argv, string standardInput, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var psi = SudoProcessStartInfo.Create();
        foreach (var argument in argv) psi.ArgumentList.Add(argument);
        return ProcessRunner.RunAsync(psi, timeoutSeconds, onOutput, logger, ct, standardInput);
    }

    /// <summary>The exact <c>sudo</c> argv of a module operation, word for word a line of the aetheus-apache grant;
    /// null for any other kind. The module name is never part of it.</summary>
    internal static IReadOnlyList<string>? BuildModuleHelperArgv(OperationKind kind) => kind switch
    {
        OperationKind.ApacheEnableModule => ["-n", ModuleHelperPath, "enable"],
        OperationKind.ApacheDisableModule => ["-n", ModuleHelperPath, "disable"],
        _ => null
    };

    // R-119: enable or disable a module through the helper. The name is checked against the shared contract here,
    // and again by the helper, which reads it on stdin.
    private async Task<ExecutorResult> ExecuteModuleOperationAsync(
        OperationKind kind, string module, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (await RejectUnsupportedPlatformAsync(onOutput).ConfigureAwait(false))
            return new ExecutorResult(-1, false);
        if (!OperationTargetValidator.IsValid(kind, module))
        {
            await onOutput($"Invalid Apache module name '{module}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var argv = BuildModuleHelperArgv(kind)!;
        var timeout = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        var run = RunModuleHelperAsync ?? RunModuleHelperWithSudoAsync;
        return await run(argv, module + "\n", timeout, onOutput, ct).ConfigureAwait(false);
    }

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        // Item #5.3: SaveConfig is the only single-shot Apache op that needs env vars (base64 payload).
        if (kind == OperationKind.ApacheSaveConfig)
            return SaveConfigAsync(target, envVars, onOutput, cancellationToken);
        // type: apache-proxy - composite: write the vhost, enable the site, reload Apache.
        if (kind == OperationKind.ApacheConfigureProxy)
            return ConfigureProxyAsync(target, envVars, timeoutSeconds, onOutput, cancellationToken);
        if (kind == OperationKind.ApacheApplyConfigSet)
            return ApplyConfigSetAsync(envVars, timeoutSeconds, onOutput, cancellationToken);
        if (kind is OperationKind.ApacheEnableSite or OperationKind.ApacheDisableSite or OperationKind.ApacheDeleteSite)
            return ExecuteSiteOperationAsync(kind, target, timeoutSeconds, onOutput, cancellationToken);
        if (kind is OperationKind.ApacheEnableModule or OperationKind.ApacheDisableModule)
            return ExecuteModuleOperationAsync(kind, target, timeoutSeconds, onOutput, cancellationToken);
        // Typed read/write of vhost config and .htaccess (replace the dead cat/printf shell builders).
        // These are pure File read/writes (like SaveConfig) - routed here, not through the no-env overload
        // whose Linux guard would short-circuit them.
        if (kind == OperationKind.ApacheGetConfig)
            return GetConfigAsync(target, envVars, onOutput, cancellationToken);
        if (kind == OperationKind.ApacheGetHtaccess)
            return GetHtaccessAsync(target, onOutput, cancellationToken);
        if (kind == OperationKind.ApacheSaveHtaccess)
            return SaveHtaccessAsync(target, envVars, onOutput, cancellationToken);
        return ExecuteAsync(kind, target, timeoutSeconds, onOutput, cancellationToken);
    }

    /// <summary>Reads where a link points (null for anything that is not a link); replaceable because a
    /// Windows test host cannot create symbolic links.</summary>
    internal Func<string, string?> ReadLinkTarget { get; init; } = static path => new FileInfo(path).LinkTarget;

    // Recette R-12: enable, disable or delete a site, then configtest and reload (ApacheSiteOperations).
    private Task<ExecutorResult> ExecuteSiteOperationAsync(
        OperationKind kind, string siteFile, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
        => new ApacheSiteOperations(
                SitesAvailablePath,
                SitesEnabledPath,
                CreateSiteSymbolicLink,
                RunPrivilegedOperationAsync,
                ReadLinkTarget,
                IsLinux)
            .ExecuteAsync(kind, siteFile, Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds), onOutput, ct);

    /// <summary>
    /// type: apache-proxy - applies a reverse-proxy vhost on the host in one step: write the rendered
    /// vhost to <c>sites-available/&lt;site&gt;</c> (ACL +rw), enable it by symlinking into
    /// <c>sites-enabled</c> (also ACL +rw), then apply via <c>systemctl reload apache2.service</c>
    /// (controlled-sudo), which both reloads AND validates the config. The controlled-sudo <c>noexec</c>
    /// blocks a standalone <c>apache2ctl configtest</c> wrapper run, so the systemd reload is the
    /// validation point: if the new vhost is invalid the reload fails and the running config stays live,
    /// and this method then REMOVES the just-enabled symlink so the broken vhost cannot down Apache on
    /// the next restart/reboot. Each phase reports honestly.
    /// </summary>
    private async Task<ExecutorResult> ConfigureProxyAsync(
        string siteFileName,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (await RejectUnsupportedPlatformAsync(onOutput).ConfigureAwait(false))
            return new ExecutorResult(-1, false);

        // 1) Write the vhost to sites-available (reuses the hardened SaveConfig path: b64 decode,
        //    path-traversal guard, direct ACL write).
        var save = await SaveConfigAsync(siteFileName, envVars, onOutput, ct).ConfigureAwait(false);
        if (save.ExitCode != 0)
            return save;

        // 2) Enable the site by symlinking it into sites-enabled (idempotent). Direct symlink rather
        //    than `a2ensite` keeps this argv/syscall-only (no perl, no extra sudo grant); requires the
        //    ACL +rw on sites-enabled granted by `install-agent-linux.sh --enable-apache-manage`.
        if (!Directory.Exists(SitesEnabledPath))
        {
            await onOutput($"{SitesEnabledPath} not found - Apache may not be installed.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        var linkPath = Path.Combine(SitesEnabledPath, siteFileName);
        var srcPath = Path.Combine(SitesAvailablePath, siteFileName);
        try
        {
            if (File.Exists(linkPath) || Directory.Exists(linkPath))
                File.Delete(linkPath);
            CreateSiteSymbolicLink(linkPath, srcPath);
            await onOutput($"Enabled site: {linkPath} -> {srcPath}", TaskLogLevel.Info).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            await onOutput($"Could not enable site at {linkPath}: {ex.Message}. Did you install with --enable-apache-manage?", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        // 3) Apply via `systemctl reload` (graceful). systemd runs ExecReload (apache2ctl graceful) as
        //    PID 1 - outside the agent's controlled-sudo `noexec` context - so it actually execs, AND it
        //    validates the config: a broken vhost makes the reload fail while the old config stays live
        //    (never a downed server). A standalone `apache2ctl configtest` can't run here because noexec
        //    blocks the wrapper script from exec'ing apache2, so the reload is the validation point.
        var reload = await ExecuteAsync(OperationKind.ApacheReload, "-", timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (reload.ExitCode != 0)
        {
            // The reload rejected the new vhost. The running config stays live, but the symlink we just
            // created still points at the broken file: leaving it would make Apache refuse to start on
            // the NEXT restart/reboot. Roll the enable back so a failed apply can't down the server later.
            try
            {
                if (File.Exists(linkPath) || Directory.Exists(linkPath))
                {
                    File.Delete(linkPath);
                    await onOutput($"Reload failed - disabled the new site (removed {linkPath}) so the broken vhost cannot down Apache on restart.", TaskLogLevel.Warning).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                await onOutput($"Reload failed and could not remove the symlink {linkPath}: {ex.Message}. Disable it manually before restarting Apache.", TaskLogLevel.Error).ConfigureAwait(false);
            }
        }
        return reload;
    }

    private async Task<ExecutorResult> ApplyConfigSetAsync(
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
        => await new ApacheConfigSetApplier(
                SitesAvailablePath,
                SitesEnabledPath,
                CreateSiteSymbolicLink,
                RunPrivilegedOperationAsync,
                PrivilegedOperationRunner is not null)
            .ApplyAsync(envVars, timeoutSeconds, onOutput, ct)
            .ConfigureAwait(false);

    private Task<ExecutorResult> RunPrivilegedOperationAsync(
        OperationKind kind,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
        => PrivilegedOperationRunner is not null
            ? PrivilegedOperationRunner(kind, timeoutSeconds, onOutput, ct)
            : ExecuteAsync(kind, "-", timeoutSeconds, onOutput, ct);

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (await RejectUnsupportedPlatformAsync(onOutput).ConfigureAwait(false))
            return new ExecutorResult(-1, false);

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        // Item #8 - log reading is a separate code path: we don't go through apache2ctl, we
        // read the log file directly with `tail` (argv-only, no shell, no allow-list dance).
        // Falls back to /var/log/httpd/* when the Debian-style path is missing.
        if (kind == OperationKind.ApacheGetLogs)
            return await GetLogsAsync(target, timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);

        // Item #5.2/#5.3/#6 - service-control + test config go through the controlled-sudo
        // recipe (sudoers file deposited by `install-agent-linux.sh --enable-apache-manage`).
        // Argv is EXACT and matches the Cmnd_Alias entries one-to-one - anything else is
        // refused by sudo at the OS level.
        return await RunProcessAsync(kind, timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExecutorResult> RunProcessAsync(
        OperationKind kind,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var psi = BuildPsi(kind);
        return psi is null
            ? new ExecutorResult(-1, false)
            : await RunProcessWithLoggerAsync(
                psi, timeoutSeconds,
                (line, level) => onOutput(line, kind == OperationKind.ApacheTestConfig ? ConfigTestLevel(line, level) : level),
                logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>R-119: <c>apache2ctl configtest</c> prints its verdict "Syntax OK" on stderr, which the process
    /// runner reports as a warning. A passing check is information, not a warning; every other line keeps its level.</summary>
    internal static TaskLogLevel ConfigTestLevel(string line, TaskLogLevel level) =>
        level == TaskLogLevel.Warning && line.Trim().EndsWith("Syntax OK", StringComparison.Ordinal)
            ? TaskLogLevel.Info
            : level;

    private async Task<bool> RejectUnsupportedPlatformAsync(
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (IsLinux) return false;

        await onOutput(
            "Apache operations are only supported on Linux",
            TaskLogLevel.Error).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Maps an Apache operation kind to a fully-resolved argv. Each entry corresponds 1:1 to a
    /// line in the <c>/etc/sudoers.d/aetheus-apache</c> allow-list - adding a new kind here
    /// requires extending the sudoers file too (justified per the
    /// <c>Controlled Sudo Escalation</c> recipe).
    /// </summary>
    internal static ProcessStartInfo? BuildPsi(OperationKind kind)
    {
        var psi = SudoProcessStartInfo.Create();
        psi.ArgumentList.Add("-n"); // never prompt - NOPASSWD is required by the sudoers rule

        switch (kind)
        {
            case OperationKind.ApacheReload:
                // systemctl reload (ExecReload = graceful) instead of `apache2ctl graceful`: the
                // controlled-sudo recipe sets `noexec`, which blocks the apache2ctl wrapper script from
                // exec'ing the apache2 binary. systemd performs the reload itself (PID 1, outside the
                // agent's noexec sudo context), so this works while keeping the recipe intact. Also
                // allow-listed in /etc/sudoers.d/aetheus-apache.
                psi.ArgumentList.Add("/bin/systemctl");
                psi.ArgumentList.Add("reload");
                psi.ArgumentList.Add("apache2.service");
                return psi;
            case OperationKind.ApacheTestConfig:
                psi.ArgumentList.Add("/usr/local/lib/aetheus/aetheus-apache-configtest");
                return psi;
            case OperationKind.ApacheStart:
                psi.ArgumentList.Add("/bin/systemctl");
                psi.ArgumentList.Add("start");
                psi.ArgumentList.Add("apache2.service");
                return psi;
            case OperationKind.ApacheStop:
                psi.ArgumentList.Add("/bin/systemctl");
                psi.ArgumentList.Add("stop");
                psi.ArgumentList.Add("apache2.service");
                return psi;
            case OperationKind.ApacheRestart:
                psi.ArgumentList.Add("/bin/systemctl");
                psi.ArgumentList.Add("restart");
                psi.ArgumentList.Add("apache2.service");
                return psi;
            default:
                return null;
        }
    }

    /// <summary>
    /// Item #5.3 - writes a vhost config file by direct <see cref="File.WriteAllBytes"/> to
    /// <c>/etc/apache2/sites-available/{target}</c>. No shell, no sudo: relies on the ACL +rw
    /// granted by the install script's <c>--enable-apache-manage</c> flag. The parent dir is
    /// hard-coded - even a regex bypass on the target filename cannot escape it.
    /// </summary>
    private async Task<ExecutorResult> SaveConfigAsync(
        string siteFileName,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (!envVars.TryGetValue("AETHEUS_APACHE_CONFIG_B64", out var b64) || string.IsNullOrEmpty(b64))
        {
            await onOutput("Missing AETHEUS_APACHE_CONFIG_B64 env var.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        if (!Directory.Exists(SitesAvailablePath))
        {
            await onOutput($"{SitesAvailablePath} not found - Apache may not be installed.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        // Path.Combine pinned to SitesAvailablePath; we then verify the resolved absolute path
        // still lives under SitesAvailablePath. Belt-and-braces: even if siteFileName slipped a
        // "../" past the regex, the final realpath check refuses to write outside.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(SitesAvailablePath));
        var dest = Path.GetFullPath(Path.Combine(SitesAvailablePath, siteFileName));
        if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            await onOutput("Path traversal blocked.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var content = await DecodeBase64Async(b64, onOutput).ConfigureAwait(false);
        if (content is null) return new ExecutorResult(-1, false);

        try
        {
            await File.WriteAllBytesAsync(dest, content, ct).ConfigureAwait(false);
            await onOutput($"Wrote {content.Length} bytes to {dest}", TaskLogLevel.Info).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }
        catch (UnauthorizedAccessException ex)
        {
            await onOutput($"Permission denied writing to {dest}. Did you run install-agent-linux.sh --enable-apache-manage? ({ex.Message})", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
    }

    /// <summary>
    /// Typed read of a vhost config file. Reads <c>{configRoot}/sites-available/{site}</c> (or the
    /// <c>{configRoot}/conf.d/{site}</c> fallback) directly via <see cref="File.ReadAllTextAsync"/> - read
    /// ACL, no shell, no <c>cat || cat</c> chain. The site name is re-validated against the shared contract
    /// and carries no slashes, so it cannot escape the config root.
    /// </summary>
    private static async Task<ExecutorResult> GetConfigAsync(
        string siteName,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (!OperationTargetValidator.IsValid(OperationKind.ApacheGetConfig, siteName))
        {
            await onOutput($"Invalid Apache site name '{siteName}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        // The config root travels in env; default to the Debian layout. Site name has no slashes.
        var configRoot = envVars.GetValueOrDefault("AETHEUS_APACHE_CONFIG_ROOT", "/etc/apache2");
        string[] candidates =
        [
            Path.Combine(configRoot, "sites-available", siteName),
            Path.Combine(configRoot, "conf.d", siteName)
        ];
        var path = Array.Find(candidates, File.Exists);
        if (path is null)
        {
            await onOutput($"No Apache config found for '{siteName}' under {configRoot}.", TaskLogLevel.Warning).ConfigureAwait(false);
            return new ExecutorResult(0, false); // empty success - nothing to show, not a failure
        }

        return await ReadAndReportAsync(path, File.ReadAllTextAsync, onOutput, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Typed read of a document root's <c>.htaccess</c>. Reads <c>{documentRoot}/.htaccess</c> directly via
    /// <see cref="File.ReadAllTextAsync"/> (empty success when absent) - no <c>cat || echo</c> shell chain.
    /// </summary>
    private async Task<ExecutorResult> GetHtaccessAsync(
        string documentRoot,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (!OperationTargetValidator.IsValid(OperationKind.ApacheGetHtaccess, documentRoot))
        {
            await onOutput($"Invalid document root '{documentRoot}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var path = Path.Combine(documentRoot, ".htaccess");
        if (!HtaccessFileExists(path))
        {
            await onOutput(string.Empty, TaskLogLevel.Info).ConfigureAwait(false); // absent = empty, honest
            return new ExecutorResult(0, false);
        }
        return await ReadAndReportAsync(path, ReadHtaccessAsync, onOutput, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Typed write of a document root's <c>.htaccess</c> from a base64 payload via
    /// <see cref="File.WriteAllBytesAsync"/> - same non-sudo access model as the old shell (the agent
    /// user must own/write the document root), minus the <c>printf | base64 -d ></c> metacharacters.
    /// </summary>
    private async Task<ExecutorResult> SaveHtaccessAsync(
        string documentRoot,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (!OperationTargetValidator.IsValid(OperationKind.ApacheSaveHtaccess, documentRoot))
        {
            await onOutput($"Invalid document root '{documentRoot}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        if (!envVars.TryGetValue("AETHEUS_APACHE_HTACCESS_B64", out var b64) || string.IsNullOrEmpty(b64))
        {
            await onOutput("Missing AETHEUS_APACHE_HTACCESS_B64 env var.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var content = await DecodeBase64Async(b64, onOutput).ConfigureAwait(false);
        if (content is null) return new ExecutorResult(-1, false);

        var path = Path.Combine(documentRoot, ".htaccess");
        try
        {
            await WriteHtaccessAsync(path, content, ct).ConfigureAwait(false);
            await onOutput($"Wrote {content.Length} bytes to {path}", TaskLogLevel.Info).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            await onOutput($"Could not write {path}: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
    }

    private static async Task<byte[]?> DecodeBase64Async(
        string value,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            await onOutput($"Invalid base64 content: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<ExecutorResult> ReadAndReportAsync(
        string path,
        Func<string, CancellationToken, Task<string>> readAsync,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        try
        {
            var content = await readAsync(path, ct).ConfigureAwait(false);
            await onOutput(content, TaskLogLevel.Info).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            await onOutput($"Could not read {path}: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
    }

    /// <summary>
    /// Tails the Apache <c>error.log</c> or <c>access.log</c>. Resolves the platform path
    /// (Debian vs RHEL) using <see cref="File.Exists"/> rather than a shell <c>||</c> chain so
    /// the operation stays inside the typed-op envelope (no allow-list bypass surface).
    /// </summary>
    private async Task<ExecutorResult> GetLogsAsync(
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        // The target string is the log type (already validated by OperationTargetValidator).
        // Bound the size in argv to keep tail predictable even on a misconfigured caller.
        var fileName = target switch
        {
            "error" => "error.log",
            "access" => "access.log",
            _ => "error.log"
        };
        var altFileName = fileName.Replace(".log", "_log"); // RHEL convention: error_log / access_log

        string? logPath =
            LogFileExists($"/var/log/apache2/{fileName}") ? $"/var/log/apache2/{fileName}"
            : LogFileExists($"/var/log/httpd/{altFileName}") ? $"/var/log/httpd/{altFileName}"
            : null;

        if (logPath is null)
        {
            await onOutput($"No Apache {target} log found at /var/log/apache2/ or /var/log/httpd/", TaskLogLevel.Warning).ConfigureAwait(false);
            return new ExecutorResult(0, false); // empty success - not a failure, just nothing to show
        }

        var psi = new ProcessStartInfo
        {
            FileName = "tail",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("100"); // Fixed cap on the agent - the front knob is advisory only.
        psi.ArgumentList.Add(logPath);

        return await RunProcessWithLoggerAsync(psi, timeoutSeconds, onOutput, logger, ct).ConfigureAwait(false);
    }
}
