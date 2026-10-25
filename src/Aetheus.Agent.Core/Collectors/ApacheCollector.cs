// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Apache introspection collector. Every external call goes through the shell-free
/// <see cref="IShellRunner.RunExecAsync"/> (argv, no <c>bash -c</c>, no interpolation) and
/// filesystem checks use <see cref="Directory"/> directly - there is no command-injection
/// surface and no privilege elevation. When the non-root agent cannot read the Apache config,
/// the result is flagged degraded with a diagnostic instead of escalating via sudo.
/// </summary>
public sealed partial class ApacheCollector(ILogger<ApacheCollector> logger, IShellRunner shell)
    : BaseShellCollector<ApacheCollector>(logger, shell), IApacheCollector
{
    private static readonly string[] LinuxBinaries = ["apache2", "httpd"];
    private static readonly string[] LinuxCtlBinaries = ["apache2ctl", "apachectl"];

    internal bool IsWindows { get; init; } = OperatingSystem.IsWindows();
    internal Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;
    internal string SitesEnabledDirectory { get; init; } = "/etc/apache2/sites-enabled";
    internal string SitesAvailableDirectory { get; init; } = "/etc/apache2/sites-available";
    internal Func<string, string?> ReadLinkTarget { get; init; } = static path => new FileInfo(path).LinkTarget;

    /// <summary>
    /// Recette R-12: <c>apache2ctl -S</c> lists only the loaded sites, so a site disabled from the page vanished
    /// from it and could not be deleted. The sites-available files no sites-enabled entry names or links to are
    /// read too, and reported disabled.
    /// </summary>
    private IReadOnlyList<ApacheVirtualHostDto> DisabledSites()
    {
        if (IsWindows || !DirectoryExists(SitesAvailableDirectory) || !DirectoryExists(SitesEnabledDirectory))
            return [];
        try
        {
            return ApacheDisabledSites.Read(SitesAvailableDirectory, SitesEnabledDirectory, ReadLinkTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug(ex, "Failed to read the disabled Apache sites");
            return [];
        }
    }

    public async Task<ApacheDataDto> CollectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string? binary = null;
        try
        {
            (binary, var ctlBinary) = await DetectBinariesAsync(ct).ConfigureAwait(false);
            if (binary is null)
                return new ApacheDataDto { IsInstalled = false };

            // Never fall back to bare `apache2 -S` (broken on Debian without APACHE_* env).
            // When `which` found no control binary, best-effort `apache2ctl` from PATH and let
            // the degraded signal speak if it is absent.
            var ctl = ctlBinary ?? (IsWindows ? binary : "apache2ctl");

            var versionTask = CollectVersionAsync(binary, ct);
            var runningTask = CollectRunningStateAsync(ct);
            var modulesTask = CollectModulesAsync(ctl, ct);
            var vhostsTask = CollectVirtualHostsAsync(ctl, ct);
            var configRootTask = DetectConfigRootAsync(ct);

            await Task.WhenAll(versionTask, runningTask, modulesTask, vhostsTask, configRootTask).ConfigureAwait(false);

            var (isRunning, pid) = await runningTask.ConfigureAwait(false);
            var (vhosts, degraded, diagnostics) = await vhostsTask.ConfigureAwait(false);

            return new ApacheDataDto
            {
                IsInstalled = true,
                IsRunning = isRunning,
                Version = await versionTask.ConfigureAwait(false),
                Pid = pid,
                ConfigRoot = await configRootTask.ConfigureAwait(false),
                Modules = await modulesTask.ConfigureAwait(false),
                VirtualHosts = vhosts,
                CollectionDegraded = degraded,
                CollectionDiagnostics = diagnostics
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // F-ENG-06: if the binary was detected, Apache IS installed - a later partial-collection failure
            // must not be reported as "not installed", and it must be visible (LogWarning), not hidden in Debug.
            var installed = binary is not null;
            Logger.LogWarning(ex, installed
                ? "Apache data collection failed after detecting the binary; reporting installed but degraded"
                : "Apache detection failed");
            return new ApacheDataDto
            {
                IsInstalled = installed,
                CollectionDegraded = installed,
                CollectionDiagnostics = installed ? "Apache data collection failed; see agent logs." : string.Empty
            };
        }
    }

    private async Task<(string? Binary, string? CtlBinary)> DetectBinariesAsync(CancellationToken ct)
    {
        if (IsWindows)
        {
            var res = await Shell.RunExecAsync("where", ["httpd.exe"], ct).ConfigureAwait(false);
            var path = res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return res.ExitCode == 0 && path is { Length: > 0 } ? (path, path) : (null, null);
        }

        foreach (var bin in LinuxBinaries)
        {
            var res = await Shell.RunExecAsync("which", [bin], ct).ConfigureAwait(false);
            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StdOut))
                continue;

            string? ctlBin = null;
            foreach (var ctl in LinuxCtlBinaries)
            {
                var ctlRes = await Shell.RunExecAsync("which", [ctl], ct).ConfigureAwait(false);
                if (ctlRes.ExitCode == 0 && !string.IsNullOrWhiteSpace(ctlRes.StdOut))
                {
                    ctlBin = ctl;
                    break;
                }
            }
            return (bin, ctlBin);
        }

        return (null, null);
    }

    private async Task<string> CollectVersionAsync(string binary, CancellationToken ct)
    {
        try
        {
            var res = await Shell.RunExecAsync(binary, ["-v"], ct).ConfigureAwait(false);
            // "Server version: Apache/2.4.58 (Ubuntu)"
            var match = VersionRegex().Match(res.StdOut);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Failed to get Apache version");
            return string.Empty;
        }
    }

    private async Task<(bool IsRunning, int? Pid)> CollectRunningStateAsync(CancellationToken ct)
    {
        try
        {
            return IsWindows
                ? await CollectRunningStateWindowsAsync(ct).ConfigureAwait(false)
                : await CollectRunningStateLinuxAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Failed to check Apache running state");
            return (false, null);
        }
    }

    private async Task<(bool IsRunning, int? Pid)> CollectRunningStateWindowsAsync(CancellationToken ct)
    {
        var res = await Shell.RunExecAsync("tasklist",
            ["/FI", "IMAGENAME eq httpd.exe", "/FO", "CSV", "/NH"], ct).ConfigureAwait(false);
        if (!res.StdOut.Contains("httpd.exe", StringComparison.OrdinalIgnoreCase))
            return (false, null);

        var parts = res.StdOut.Split(',');
        if (parts.Length >= 2 && int.TryParse(parts[1].Trim('"', ' '), out var pid))
            return (true, pid);

        return (true, null);
    }

    private async Task<(bool IsRunning, int? Pid)> CollectRunningStateLinuxAsync(CancellationToken ct)
    {
        var active = false;
        foreach (var unit in LinuxBinaries) // "apache2", "httpd"
        {
            var res = await Shell.RunExecAsync("systemctl", ["is-active", unit], ct).ConfigureAwait(false);
            if (res.StdOut.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                active = true;
                break;
            }
        }
        if (!active)
            return (false, null);

        var pidRes = await Shell.RunExecAsync("pgrep", ["-o", "apache2|httpd"], ct).ConfigureAwait(false);
        return int.TryParse(pidRes.StdOut.Trim(), out var pid) ? (true, pid) : (true, null);
    }

    private async Task<List<ApacheModuleDto>> CollectModulesAsync(string ctl, CancellationToken ct)
    {
        var result = new List<ApacheModuleDto>();
        try
        {
            var (output, _, _) = await DumpAsync("-M", ctl, ct).ConfigureAwait(false);

            // " rewrite_module (shared)" or " core_module (static)"
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = ModuleRegex().Match(line);
                if (!match.Success) continue;

                result.Add(new ApacheModuleDto
                {
                    Name = match.Groups[1].Value,
                    Type = match.Groups[2].Value,
                    IsEnabled = true
                });
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Failed to collect Apache modules");
        }
        return result;
    }

    private async Task<(List<ApacheVirtualHostDto> Vhosts, bool Degraded, string Diagnostics)> CollectVirtualHostsAsync(string ctl, CancellationToken ct)
    {
        var result = new List<ApacheVirtualHostDto>();
        var degraded = false;
        var diagnostics = string.Empty;
        try
        {
            var (output, failed, diag) = await DumpAsync("-S", ctl, ct).ConfigureAwait(false);
            degraded = failed;
            diagnostics = diag;

            // Parse lines like:
            //   port 80 namevhost example.com (/etc/apache2/sites-enabled/example.conf:1)
            //   *:443                  example.com (/etc/apache2/sites-enabled/example-ssl.conf:1)
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = VHostRegex().Match(line);
                if (!match.Success) continue;

                var portStr = match.Groups[1].Success && match.Groups[1].Length > 0
                    ? match.Groups[1].Value
                    : match.Groups[2].Value;
                var serverName = match.Groups[3].Value;
                var configFile = match.Groups[4].Value;

                _ = int.TryParse(portStr, CultureInfo.InvariantCulture, out var port);

                result.Add(new ApacheVirtualHostDto
                {
                    ServerName = serverName,
                    Port = port,
                    ConfigFile = configFile,
                    IsEnabled = true
                });
            }

            await EnrichWithDocumentRootsAsync(result, ct).ConfigureAwait(false);
            EnrichWithEnabledState(result);
            result.AddRange(DisabledSites());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Failed to collect Apache virtual hosts");
        }
        return (result, degraded, diagnostics);
    }

    private async Task EnrichWithDocumentRootsAsync(List<ApacheVirtualHostDto> vhosts, CancellationToken ct)
    {
        for (var i = 0; i < vhosts.Count; i++)
        {
            var vhost = vhosts[i];
            if (string.IsNullOrEmpty(vhost.ConfigFile)) continue;

            try
            {
                var configPath = StripLineSuffix(vhost.ConfigFile);

                // Defence-in-depth: argv execution already removes shell-injection risk, but the
                // path is parsed from `apache2ctl -S` output (a possibly multi-tenant config
                // tree) so still reject anything that is not a plain absolute path.
                if (!SafeConfigPathRegex().IsMatch(configPath))
                {
                    Logger.LogWarning("Skipping unsafe Apache config path: {Path}", configPath);
                    continue;
                }

                var content = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
                var match = DocumentRootRegex().Match(content);
                if (match.Success)
                    vhosts[i] = vhost with { DocumentRoot = match.Groups[1].Value };
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Logger.LogDebug(ex, "Failed to read DocumentRoot for vhost {VHost}", vhost.ServerName);
            }
        }
    }

    private void EnrichWithEnabledState(List<ApacheVirtualHostDto> vhosts)
    {
        if (IsWindows) return;

        try
        {
            if (!DirectoryExists(SitesEnabledDirectory)) return;

            HashSet<string> enabledSites;
            try
            {
                enabledSites = new HashSet<string>(
                    Directory.EnumerateFileSystemEntries(SitesEnabledDirectory).Select(Path.GetFileName)!,
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (UnauthorizedAccessException)
            {
                return; // non-root agent without read access - leave IsEnabled at its default
            }

            for (var i = 0; i < vhosts.Count; i++)
            {
                var vhost = vhosts[i];
                var configFileName = Path.GetFileName(StripLineSuffix(vhost.ConfigFile));
                vhosts[i] = vhost with { IsEnabled = enabledSites.Contains(configFileName) };
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to detect enabled sites");
        }
    }

    private async Task<string> DetectConfigRootAsync(CancellationToken ct)
    {
        try
        {
            if (IsWindows)
            {
                var res = await Shell.RunExecAsync("httpd.exe", ["-V"], ct).ConfigureAwait(false);
                var match = ServerRootRegex().Match(res.StdOut);
                return match.Success ? match.Groups[1].Value : string.Empty;
            }

            // Debian-style first, then RHEL-style - plain filesystem check, no shell.
            foreach (var dir in new[] { "/etc/apache2", "/etc/httpd" })
            {
                if (DirectoryExists(dir))
                    return dir;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Failed to detect Apache config root");
        }
        return string.Empty;
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="ctl"/> with <paramref name="flag"/> (e.g. <c>-S</c> / <c>-M</c>)
    /// shell-free and reports whether the enumeration is trustworthy. Read-only introspection
    /// is run <b>without elevation</b>; <c>apache2ctl</c> is a wrapper that sources
    /// <c>/etc/apache2/envvars</c> and parses the full config, so granting it sudo would be a
    /// root-escalation primitive. A failure is surfaced as a degraded signal (the UI explains
    /// how to grant the agent read access) instead of escalating privileges.
    /// </summary>
    private async Task<(string Output, bool Degraded, string Diagnostics)> DumpAsync(string flag, string ctl, CancellationToken ct)
    {
        var res = await Shell.RunExecAsync(ctl, [flag], ct).ConfigureAwait(false);
        if (res.ExitCode == 0)
        {
            Logger.LogDebug("{Ctl} {Flag} ok, {Bytes} bytes", ctl, flag, res.StdOut.Length);
            return (res.StdOut, false, string.Empty);
        }

        var diag = $"`{ctl} {flag}` exited {res.ExitCode} - the agent (non-root) cannot read the Apache " +
                   "configuration. Grant the agent's user read access to the config tree (add it to a group " +
                   "that can read /etc/apache2, or set a read ACL). No sudo/root is required for read-only " +
                   "introspection and must not be granted.";
        Logger.LogWarning("Apache collection degraded: {Diagnostics}", diag);
        return (res.StdOut, true, diag);
    }

    /// <summary>Strips the trailing <c>:line</c> suffix Apache appends to config paths.</summary>
    private static string StripLineSuffix(string configFile) =>
        configFile.Contains(':') ? configFile[..configFile.LastIndexOf(':')] : configFile;

    // "Server version: Apache/2.4.58 (Ubuntu)"
    [GeneratedRegex(@"Apache/([\d.]+)")]
    private static partial Regex VersionRegex();

    // " rewrite_module (shared)" or " core_module (static)"
    [GeneratedRegex(@"^\s+(\w+_module)\s+\((static|shared)\)", RegexOptions.Multiline)]
    private static partial Regex ModuleRegex();

    // "port 80 namevhost example.com (/etc/apache2/sites-enabled/example.conf:1)"
    // "*:443                  is a NameVirtualHost"
    [GeneratedRegex(@"(?:port\s+(\d+)\s+namevhost|^\s*\*:(\d+)\s+)\s+(\S+)\s+\((.+?)(?::\d+)?\)", RegexOptions.Multiline)]
    private static partial Regex VHostRegex();

    // "DocumentRoot /var/www/html"
    [GeneratedRegex(@"DocumentRoot\s+[""']?(.+?)[""']?\s*$", RegexOptions.Multiline)]
    private static partial Regex DocumentRootRegex();

    // HTTPD_ROOT="/etc/apache2" or SERVER_CONFIG_FILE
    [GeneratedRegex(@"HTTPD_ROOT=""?(.+?)""?\s*$", RegexOptions.Multiline)]
    private static partial Regex ServerRootRegex();

    // Plain absolute config path only: no spaces, no shell metacharacters ($ ` ; | & etc.).
    [GeneratedRegex(@"^/[A-Za-z0-9._/-]+$")]
    private static partial Regex SafeConfigPathRegex();
}
