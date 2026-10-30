// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Recette R-97 (ADR-058): finds and removes what a project deployed on this host when the project is deleted.
/// The task's target is the project id; the identity travels in <see cref="ProjectServerResourceSpec"/> and is
/// validated again here (strict names, state directories confined to <c>/var/lib/aetheus-agent/projects/&lt;name&gt;</c> or <c>/var/lib/&lt;name&gt;</c>, never a system
/// directory), because the task is not a trust boundary.
/// <list type="bullet">
/// <item><description>Discovery reads, without changing anything: <c>docker ps -a</c>, <c>docker volume ls</c> and
/// <c>docker network ls</c> and <c>docker image ls</c> filtered by the label <c>com.docker.compose.project=&lt;project&gt;</c>;
/// the regular files <c>&lt;host&gt;.conf</c> and <c>&lt;host&gt;-ssl.conf</c> of <c>sites-available</c>, and the blue-green
/// upstream file (<c>UPSTREAM_CONF</c>); the certificate lineage of each host
/// (<c>/etc/letsencrypt/live/&lt;host&gt;</c> or <c>renewal/&lt;host&gt;.conf</c>); the state directories and the backup
/// archive directories. Something it cannot read is printed <c>UNREADABLE</c>, never taken for absent.</description></item>
/// <item><description>Removal reads the host again and removes only what is both in the confirmed list and still there,
/// in order: <c>docker rm -f</c>, <c>docker volume rm</c>, <c>docker network rm</c> (argv, no shell), each virtual host
/// disabled then deleted through the site operations (configtest and reload, put back on failure), each certificate
/// through <c>sudo -n aetheus-certbot-manage delete &lt;host&gt;</c>, then the directories, then <c>docker image rm</c>
/// (never forced, refused while a container uses the image), then the upstream file like a virtual host, refused
/// while an enabled site uses one of its <c>Define</c>. One line per resource
/// (<c>REMOVED</c>, <c>ABSENT</c>, <c>FAILED</c>, <c>NOTLISTED</c>); the task fails unless every one is gone.</description></item>
/// </list>
/// </summary>
public sealed class ProjectResourcesOperationExecutor(
    ApacheOperationExecutor apache,
    CertbotOperationExecutor certbot,
    IOptions<AetheusAgentOptions> options,
    ILogger<ProjectResourcesOperationExecutor> logger) : IOperationExecutor
{
    private const int DockerTimeoutSeconds = 120;
    private const string ComposeLabel = "label=com.docker.compose.project=";
    private const string ProcessLinePrefix = "[process] ";
    private const string StderrLinePrefix = "[stderr] ";
    private static readonly IReadOnlyDictionary<string, string> s_noEnvironment = new Dictionary<string, string>();

    private readonly AetheusAgentOptions _options = options.Value;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    internal string SitesAvailablePath { get; init; } = "/etc/apache2/sites-available";
    internal string SitesEnabledPath { get; init; } = "/etc/apache2/sites-enabled";
    internal string LetsEncryptPath { get; init; } = "/etc/letsencrypt";

    /// <summary>The text of a site read directly as the agent account (sites-enabled entries are links to
    /// sites-available), null when it cannot be read that way: a site certbot wrote as root with mode 0600 is then
    /// read through <see cref="SiteReader"/>.</summary>
    internal Func<string, string?> ReadSiteFile { get; init; } = static path =>
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    };

    /// <summary>The absolute target of a sites-enabled entry that is a symbolic link, null when it is not one (or
    /// cannot be inspected).</summary>
    internal Func<string, string?> LinkTargetOf { get; init; } = static path =>
    {
        try
        {
            var target = new FileInfo(path).LinkTarget;
            return target is null ? null : Path.GetFullPath(target, Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    };

    /// <summary>Reads a site file the agent account cannot open through the root-owned read-only Apache helper.</summary>
    internal ApacheSiteFileReader SiteReader { get; init; } = new(logger);

    /// <summary>Runs <c>docker</c> with the given argv and hands back its result and output lines.</summary>
    internal Func<IReadOnlyList<string>, CancellationToken, Task<(ExecutorResult Result, IReadOnlyList<string> Lines)>> RunDockerAsync { get; init; }
        = (argv, ct) => ProcessRunner.CaptureAsync("docker", argv, DockerTimeoutSeconds, logger, ct);

    /// <summary>Runs an Apache site operation (disable, delete) through the Apache executor.</summary>
    internal Func<OperationKind, string, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>> RunSiteOperationAsync { get; init; }
        = (kind, siteFile, timeout, onOutput, ct) => apache.ExecuteAsync(kind, siteFile, s_noEnvironment, timeout, onOutput, ct);

    /// <summary>Deletes a certificate lineage through the certbot manage helper.</summary>
    internal Func<string, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>> DeleteCertificateAsync { get; init; }
        = (lineage, timeout, onOutput, ct) => certbot.ExecuteAsync(OperationKind.CertbotDelete, lineage, timeout, onOutput, ct);

    /// <summary>The names in a directory: empty when it does not exist, null when it cannot be read.</summary>
    internal Func<string, IReadOnlySet<string>?> ListDirectory { get; init; } = static path =>
    {
        if (!Directory.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    };

    internal Func<string, bool> IsRegularFile { get; init; } = static path => File.Exists(path) && new FileInfo(path).LinkTarget is null;
    internal Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;
    internal Func<string, bool> IsLink { get; init; } = static path => new DirectoryInfo(path).LinkTarget is not null;
    internal Action<string> DeleteDirectory { get; init; } = static path => Directory.Delete(path, recursive: true);

    public bool CanHandle(OperationKind kind) => kind is OperationKind.ProjectResourcesDiscover or OperationKind.ProjectResourcesRemove;

    public Task<ExecutorResult> ExecuteAsync(OperationKind kind, string target, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken) =>
        ExecuteAsync(kind, target, s_noEnvironment, timeoutSeconds, onOutput, cancellationToken);

    public async Task<ExecutorResult> ExecuteAsync(OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envVars);
        ArgumentNullException.ThrowIfNull(onOutput);
        if (!CanHandle(kind) || !OperationTargetValidator.IsValid(kind, target))
            return await FailAsync(onOutput, "Invalid project resources target").ConfigureAwait(false);
        if (!IsLinux)
            return await FailAsync(onOutput, "The project resources operations run on Linux only").ConfigureAwait(false);
        var spec = ProjectServerResourceSpec.Decode(envVars.GetValueOrDefault(ProjectServerResourceSpec.EnvironmentVariable), out var refusal);
        if (spec is null)
            return await FailAsync(onOutput, refusal ?? "Invalid project resources specification").ConfigureAwait(false);
        if (kind == OperationKind.ProjectResourcesDiscover && spec.Remove.Count > 0)
            return await FailAsync(onOutput, "A discovery does not remove anything: the removal list must be empty").ConfigureAwait(false);
        if (ControlPlaneHost() is { } controlPlane && spec.Hosts.Contains(controlPlane, StringComparer.OrdinalIgnoreCase))
            return await FailAsync(onOutput, $"Refused host '{controlPlane}': it serves the Aetheus control plane this agent reports to").ConfigureAwait(false);

        var state = await InspectAsync(spec, cancellationToken).ConfigureAwait(false);
        return kind == OperationKind.ProjectResourcesDiscover
            ? await ReportDiscoveryAsync(state, onOutput).ConfigureAwait(false)
            : await RemoveAsync(spec, state, Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds), onOutput, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The host of the control plane this agent reports to (<c>Aetheus:ServerUrl</c>), in lower case; its
    /// virtual hosts and certificate are never a project's, whatever the backend sent.</summary>
    private string? ControlPlaneHost() =>
        Uri.TryCreate(_options.ServerUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host.ToLowerInvariant() : null;

    /// <summary>What the host holds of the identity, and what could not be read.</summary>
    private sealed record HostState(List<ProjectServerResourceRef> Found, List<(ProjectServerResourceKind Kind, string Scope, string Reason)> Unreadable)
    {
        public bool IsUnreadable(ProjectServerResourceRef item) =>
            Unreadable.Any(entry => entry.Kind == item.Kind && entry.Scope == item.Scope);
    }

    private async Task<HostState> InspectAsync(ProjectServerResourceSpec spec, CancellationToken ct)
    {
        var state = new HostState([], []);
        foreach (var project in spec.ComposeProjects)
        {
            await ListDockerAsync(state, ProjectServerResourceKind.Container, project, ["ps", "-a", "--filter", ComposeLabel + project, "--format", "{{.Names}}"], ct).ConfigureAwait(false);
            await ListDockerAsync(state, ProjectServerResourceKind.Volume, project, ["volume", "ls", "-q", "--filter", ComposeLabel + project], ct).ConfigureAwait(false);
            await ListDockerAsync(state, ProjectServerResourceKind.Network, project, ["network", "ls", "--filter", ComposeLabel + project, "--format", "{{.Name}}"], ct).ConfigureAwait(false);
            // The images the Compose project built carry its label; an untagged one is named by its short id.
            await ListDockerAsync(state, ProjectServerResourceKind.Image, project,
                ["image", "ls", "--filter", ComposeLabel + project, "--format", "{{.ID}} {{.Repository}}:{{.Tag}}"], ct, ImageName).ConfigureAwait(false);
        }

        var sites = ListDirectory(SitesAvailablePath);
        InspectHosts(state, spec, sites);
        InspectUpstreamConfigurations(state, spec, sites);
        foreach (var directory in spec.StateDirectories)
            InspectDirectory(state, ProjectServerResourceKind.StateDirectory, directory, directory);
        // The repository copy a deployment unpacks (DEPLOY_DIR of the generic templates), one per Compose project.
        foreach (var project in spec.ComposeProjects)
            InspectDirectory(state, ProjectServerResourceKind.DeploymentDirectory, ProjectServerResourceNames.DeploymentDirectory(project), project);
        foreach (var policyId in spec.BackupPolicyIds)
        {
            var archives = ProjectServerResourceNames.BackupArchiveDirectory(policyId);
            InspectDirectory(state, ProjectServerResourceKind.BackupArchives, archives, archives);
        }
        return state;
    }

    /// <summary>The virtual host files and the certificate lineage of each host.</summary>
    private void InspectHosts(HostState state, ProjectServerResourceSpec spec, IReadOnlySet<string>? sites)
    {
        var live = ListDirectory($"{LetsEncryptPath}/live");
        var renewal = ListDirectory($"{LetsEncryptPath}/renewal");
        foreach (var host in spec.Hosts)
        {
            if (sites is null)
                state.Unreadable.Add((ProjectServerResourceKind.VirtualHost, host, $"{SitesAvailablePath} cannot be read"));
            else
                state.Found.AddRange(ProjectServerResourceNames.VirtualHostFiles(host)
                    .Where(file => sites.Contains(file) && IsRegularFile($"{SitesAvailablePath}/{file}"))
                    .Select(file => new ProjectServerResourceRef(ProjectServerResourceKind.VirtualHost, file, host)));

            if (live is null && renewal is null)
                state.Unreadable.Add((ProjectServerResourceKind.Certificate, host, $"{LetsEncryptPath} cannot be read"));
            else if ((live?.Contains(host) ?? false) || (renewal?.Contains($"{host}.conf") ?? false))
                state.Found.Add(new ProjectServerResourceRef(ProjectServerResourceKind.Certificate, host, host));
        }
    }

    /// <summary>The blue-green upstream files: a regular file of sites-available, anything else refused.</summary>
    private void InspectUpstreamConfigurations(HostState state, ProjectServerResourceSpec spec, IReadOnlySet<string>? sites)
    {
        foreach (var path in spec.UpstreamConfigurations)
        {
            var file = ProjectServerResourceNames.UpstreamConfigurationFile(path);
            if (sites is null)
                state.Unreadable.Add((ProjectServerResourceKind.UpstreamConfiguration, path, $"{SitesAvailablePath} cannot be read"));
            else if (!sites.Contains(file))
                continue;
            else if (IsRegularFile($"{SitesAvailablePath}/{file}"))
                state.Found.Add(new ProjectServerResourceRef(ProjectServerResourceKind.UpstreamConfiguration, path, path));
            else
                state.Unreadable.Add((ProjectServerResourceKind.UpstreamConfiguration, path, "it is not a regular file: refused"));
        }
    }

    private async Task ListDockerAsync(HostState state, ProjectServerResourceKind kind, string project, IReadOnlyList<string> argv,
        CancellationToken ct, Func<string, string>? nameOf = null)
    {
        var (result, lines) = await RunDockerAsync(argv, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut)
        {
            state.Unreadable.Add((kind, project, $"docker could not list them (exit code {result.ExitCode})"));
            return;
        }
        foreach (var name in StandardOutput(lines).Select(line => line.Trim()).Where(line => line.Length > 0)
            .Select(line => nameOf is null ? line : nameOf(line)).Distinct(StringComparer.Ordinal))
        {
            if (ProjectServerResourceNames.IsReservedForControlPlane(name))
                state.Unreadable.Add((kind, project, $"'{name}' is named like the Aetheus control plane: refused, left as it is"));
            else if (ProjectServerResourceNames.IsResource(kind, name, project))
                state.Found.Add(new ProjectServerResourceRef(kind, name, project));
            else
                state.Unreadable.Add((kind, project, $"docker printed a name that is refused: '{name}'"));
        }
    }

    /// <summary>The name of an image line <c>&lt;id&gt; &lt;repository&gt;:&lt;tag&gt;</c>: the reference, or the short id
    /// when the image has no tag (<c>&lt;none&gt;:&lt;none&gt;</c>). A line of another shape is handed back whole, and
    /// refused by the name check.</summary>
    private static string ImageName(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2) return line;
        return fields[1].Contains("<none>", StringComparison.Ordinal) ? fields[0] : fields[1];
    }

    /// <summary>What the command printed on stdout: <see cref="ProcessRunner.CaptureAsync"/> also hands back its
    /// stderr lines (prefixed <c>[stderr]</c>) and its own <c>[process] exit-code=N</c> line, which are not names.</summary>
    private static IEnumerable<string> StandardOutput(IEnumerable<string> lines) =>
        lines.Where(line => !line.StartsWith(ProcessLinePrefix, StringComparison.Ordinal)
            && !line.StartsWith(StderrLinePrefix, StringComparison.Ordinal));

    /// <summary>A directory the project left: listed when it is a plain directory, refused (never followed) when it or a
    /// parent the agent owns is a symbolic link. <paramref name="scope"/> is what the resource belongs to (its path for a
    /// state or archive directory, its Compose project for a deployment directory).</summary>
    private void InspectDirectory(HostState state, ProjectServerResourceKind kind, string path, string scope)
    {
        if (!DirectoryExists(path)) return;
        if (IsLink(path))
            state.Unreadable.Add((kind, scope, "it is a symbolic link: refused"));
        else if (LinkedParentOf(path) is { } parent)
            state.Unreadable.Add((kind, scope, $"{parent} is a symbolic link: refused"));
        else
            state.Found.Add(new ProjectServerResourceRef(kind, path, scope));
    }

    private static async Task<ExecutorResult> ReportDiscoveryAsync(HostState state, Func<string, TaskLogLevel, Task> onOutput)
    {
        foreach (var item in state.Found.OrderBy(item => item.Kind))
            await onOutput(ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.Found, item), TaskLogLevel.Info).ConfigureAwait(false);
        await WriteUnreadableAsync(state, onOutput).ConfigureAwait(false);
        await onOutput(ProjectServerResourceOutput.CompleteLine, TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    private async Task<ExecutorResult> RemoveAsync(ProjectServerResourceSpec spec, HostState state, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var listed = spec.Remove.Distinct().ToHashSet();
        var clean = state.Unreadable.Count == 0;
        // Hosts whose virtual host is not gone: their certificate must stay, or Apache fails at its next reload.
        var hostsWithSite = new HashSet<string>(StringComparer.Ordinal);
        // Kind order: virtual hosts are handled before the certificates they use.
        foreach (var item in spec.Remove.Distinct().OrderBy(item => item.Kind))
        {
            var (outcome, reason) = await OutcomeOfAsync(spec, state, item, hostsWithSite, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            if (item.Kind == ProjectServerResourceKind.VirtualHost && outcome == ProjectServerResourceOutcome.Failed)
                hostsWithSite.Add(item.Scope);
            clean &= outcome != ProjectServerResourceOutcome.Failed;
            await onOutput(ProjectServerResourceOutput.Line(outcome, item, reason), TaskLogLevel.Info).ConfigureAwait(false);
        }
        foreach (var item in state.Found.Where(item => !listed.Contains(item)).OrderBy(item => item.Kind))
        {
            clean = false;
            await onOutput(ProjectServerResourceOutput.Line(ProjectServerResourceOutcome.NotListed, item, "it appeared after the preview: left as it is"), TaskLogLevel.Info).ConfigureAwait(false);
        }
        await WriteUnreadableAsync(state, onOutput).ConfigureAwait(false);
        await onOutput(ProjectServerResourceOutput.CompleteLine, TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(clean ? 0 : 1, false);
    }

    /// <summary>What happened to one confirmed resource: already gone, removed, or failed with its reason.</summary>
    private async Task<(ProjectServerResourceOutcome Outcome, string? Reason)> OutcomeOfAsync(
        ProjectServerResourceSpec spec, HostState state, ProjectServerResourceRef item, IReadOnlySet<string> hostsWithSite,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!state.Found.Contains(item))
            return state.IsUnreadable(item)
                ? (ProjectServerResourceOutcome.Failed, "it could not be read, so it is not known to be gone")
                : (ProjectServerResourceOutcome.Absent, null);
        var blocker = item.Kind == ProjectServerResourceKind.Certificate
            ? await CertificateBlockerAsync(item.Name, hostsWithSite, ct).ConfigureAwait(false) : null;
        if (blocker is not null) return (ProjectServerResourceOutcome.Failed, blocker);
        var failure = await RemoveOneAsync(spec, item, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        return failure is null ? (ProjectServerResourceOutcome.Removed, null) : (ProjectServerResourceOutcome.Failed, failure);
    }

    /// <summary>
    /// Why the certificate of <paramref name="host"/> must not be deleted, or null. Apache refuses to start when an
    /// enabled site names a certificate file that is gone, so the lineage stays while a virtual host of the host
    /// failed to go, is still enabled, or while any enabled site still references <c>live/&lt;host&gt;/</c>.
    /// </summary>
    private async Task<string?> CertificateBlockerAsync(string host, IReadOnlySet<string> hostsWithSite, CancellationToken ct)
    {
        if (hostsWithSite.Contains(host)) return "vhost not removed";
        var enabled = ListDirectory(SitesEnabledPath);
        if (enabled is null) return $"vhost not known to be removed: {SitesEnabledPath} cannot be read";
        if (ProjectServerResourceNames.VirtualHostFiles(host).Any(enabled.Contains)) return "vhost not removed: it is still enabled";
        var lineage = $"{LetsEncryptPath}/live/{host}/";
        foreach (var site in enabled.Where(site => !IsDanglingLink(site)).Order(StringComparer.Ordinal))
        {
            var read = await ReadSiteAsync(SitesEnabledPath, site, ct).ConfigureAwait(false);
            if (read.Text is null) return $"the enabled site {site} cannot be read, so it may still use the certificate: {read.Failure}";
            if (read.Text.Contains(lineage, StringComparison.Ordinal)) return $"the enabled site {site} still uses it";
        }
        return null;
    }

    /// <summary>Removes one resource; null when it is gone, the reason otherwise.</summary>
    private async Task<string?> RemoveOneAsync(ProjectServerResourceSpec spec, ProjectServerResourceRef item, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case ProjectServerResourceKind.Container:
                return await DockerAsync(["rm", "-f", item.Name], ct).ConfigureAwait(false);
            case ProjectServerResourceKind.Volume:
                return await DockerAsync(["volume", "rm", item.Name], ct).ConfigureAwait(false);
            case ProjectServerResourceKind.Network:
                return await DockerAsync(["network", "rm", item.Name], ct).ConfigureAwait(false);
            case ProjectServerResourceKind.VirtualHost:
                var disabled = await RunSiteOperationAsync(OperationKind.ApacheDisableSite, item.Name, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
                if (disabled.ExitCode != 0) return $"the site could not be disabled (exit code {disabled.ExitCode})";
                var deleted = await RunSiteOperationAsync(OperationKind.ApacheDeleteSite, item.Name, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
                return deleted.ExitCode == 0 ? null : $"the site file could not be deleted (exit code {deleted.ExitCode})";
            case ProjectServerResourceKind.Certificate:
                var certificate = await DeleteCertificateAsync(item.Name, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
                return certificate.ExitCode == 0 ? null : $"certbot could not delete it (exit code {certificate.ExitCode})";
            case ProjectServerResourceKind.StateDirectory:
            case ProjectServerResourceKind.BackupArchives:
            case ProjectServerResourceKind.DeploymentDirectory:
                return DeleteConfinedDirectory(item, spec.Slug);
            case ProjectServerResourceKind.Image:
                return await RemoveImageAsync(item.Name, ct).ConfigureAwait(false);
            case ProjectServerResourceKind.UpstreamConfiguration:
                return await RemoveUpstreamConfigurationAsync(item.Name, spec.Slug, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
            default:
                return "unknown resource kind";
        }
    }

    /// <summary>Removes an image the Compose project built, never with <c>--force</c>: an image a container still
    /// uses (one not removed, or not the project's) stays, and the item fails with the containers' names.</summary>
    private async Task<string?> RemoveImageAsync(string image, CancellationToken ct)
    {
        var (result, lines) = await RunDockerAsync(["container", "ls", "-a", "--filter", "ancestor=" + image, "--format", "{{.Names}}"], ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut)
            return $"docker could not say which containers use it (exit code {result.ExitCode}): left as it is";
        var users = StandardOutput(lines).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        if (users.Count > 0)
            return $"still used by the container {string.Join(", ", users)}: left as it is";
        return await DockerAsync(["image", "rm", image], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the blue-green upstream file the way a virtual host goes (disabled, then deleted, through the site
    /// operations: configtest and reload, put back on failure), after checking again that it is the project's file of
    /// sites-available. While an enabled site still uses one of the <c>Define</c> it holds (<c>${W9_FRONT_PORT}</c>), the
    /// file stays and the item fails: Apache would lose the port that site proxies to.
    /// </summary>
    private async Task<string?> RemoveUpstreamConfigurationAsync(string path, string slug, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!ProjectServerResourceNames.IsUpstreamConfigurationOf(path, slug)) return "the path is outside the confinement: refused";
        var file = ProjectServerResourceNames.UpstreamConfigurationFile(path);
        var blocker = await UpstreamBlockerAsync(file, ct).ConfigureAwait(false);
        if (blocker is not null) return blocker;
        var disabled = await RunSiteOperationAsync(OperationKind.ApacheDisableSite, file, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (disabled.ExitCode != 0) return $"the upstream file could not be disabled (exit code {disabled.ExitCode})";
        var deleted = await RunSiteOperationAsync(OperationKind.ApacheDeleteSite, file, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        return deleted.ExitCode == 0 ? null : $"the upstream file could not be deleted (exit code {deleted.ExitCode})";
    }

    /// <summary>Why the upstream file must stay, or null: an enabled site still uses one of its <c>Define</c>.</summary>
    private async Task<string?> UpstreamBlockerAsync(string file, CancellationToken ct)
    {
        var upstream = await ReadSiteAsync(SitesAvailablePath, file, ct).ConfigureAwait(false);
        if (upstream.Text is null) return $"it cannot be read, so the Define it holds are not known: {upstream.Failure}";
        var defines = upstream.Text.Split('\n')
            .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 2 && string.Equals(fields[0], "Define", StringComparison.OrdinalIgnoreCase))
            .Select(fields => fields[1])
            .ToList();
        if (defines.Count == 0) return null;
        var enabled = ListDirectory(SitesEnabledPath);
        if (enabled is null) return $"{SitesEnabledPath} cannot be read, so a site may still use its Define";
        foreach (var site in enabled.Where(site => site != file && !IsDanglingLink(site)).Order(StringComparer.Ordinal))
        {
            var read = await ReadSiteAsync(SitesEnabledPath, site, ct).ConfigureAwait(false);
            if (read.Text is null) return $"the enabled site {site} cannot be read, so it may still use its Define: {read.Failure}";
            var siteText = read.Text;
            var used = defines.FirstOrDefault(define => siteText.Contains("${" + define + "}", StringComparison.Ordinal));
            if (used is not null) return $"the enabled site {site} still uses its Define {used}";
        }
        return null;
    }

    /// <summary>Whether an enabled site is a link whose target is gone from its directory: Apache cannot load it, so it
    /// uses no certificate and no Define (left by a site removed from sites-available without a2dissite). A link
    /// whose target directory cannot be listed is not called dangling: it is read, and blocks if it cannot be.</summary>
    private bool IsDanglingLink(string site)
    {
        var target = LinkTargetOf($"{SitesEnabledPath}/{site}");
        var directory = target is null ? null : Path.GetDirectoryName(target);
        var entries = directory is null ? null : ListDirectory(directory);
        return entries is not null && !entries.Contains(Path.GetFileName(target!));
    }

    /// <summary>A site file: read directly when the agent account can, otherwise through the read-only helper. A
    /// file neither way can read is a failure with its reason, never an empty text.</summary>
    private async Task<SiteFileRead> ReadSiteAsync(string directory, string site, CancellationToken ct)
    {
        var text = ReadSiteFile($"{directory}/{site}");
        return text is not null ? SiteFileRead.Of(text) : await SiteReader.ReadAsync(directory, site, ct).ConfigureAwait(false);
    }

    private async Task<string?> DockerAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var (result, lines) = await RunDockerAsync(argv, ct).ConfigureAwait(false);
        if (result.ExitCode == 0 && !result.TimedOut) return null;
        // Docker says why on stderr; the runner's own "[process] exit-code" line says nothing more than the code.
        var detail = lines.LastOrDefault(line => line.StartsWith(StderrLinePrefix, StringComparison.Ordinal)
                && line.Length > StderrLinePrefix.Length)?[StderrLinePrefix.Length..].Trim()
            ?? StandardOutput(lines).LastOrDefault(line => line.Trim().Length > 0)?.Trim();
        return $"docker {argv[0]} failed (exit code {result.ExitCode}){(detail is null ? string.Empty : ": " + detail)}";
    }

    /// <summary>Deletes a state, backup or deployment directory after checking, again, that it is confined and not a link.</summary>
    private string? DeleteConfinedDirectory(ProjectServerResourceRef item, string slug)
    {
        var confined = item.Kind switch
        {
            ProjectServerResourceKind.StateDirectory => ProjectServerResourceNames.IsStateDirectoryOf(item.Name, slug),
            ProjectServerResourceKind.DeploymentDirectory => ProjectServerResourceNames.IsComposeProjectOf(item.Scope, slug)
                && ProjectServerResourceNames.IsDeploymentDirectoryOf(item.Name, item.Scope),
            _ => ProjectServerResourceNames.IsBackupArchiveDirectory(item.Name)
        };
        if (!confined) return "the path is outside the confinement: refused";
        if (IsLink(item.Name)) return "it is a symbolic link: refused";
        if (LinkedParentOf(item.Name) is { } parent) return $"{parent} is a symbolic link: refused";
        try
        {
            DeleteDirectory(item.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported on its own line: the other resources are still removed, and the task fails.
            logger.LogWarning(ex, "Project directory {Path} could not be deleted", item.Name);
            return ex.Message;
        }
        return DirectoryExists(item.Name) ? "the directory is still there after the delete" : null;
    }

    /// <summary>For a state directory inside the agent's <c>projects</c> directory or a deployment directory inside its
    /// <c>.deployments</c> directory, the first of that directory and the agent's working directory that is a symbolic
    /// link (the delete would then follow it out of the confinement), or null. Other paths have no parent the agent
    /// owns to check.</summary>
    private string? LinkedParentOf(string path)
    {
        var container = ProjectServerResourceNames.IsInProjectStateDirectoryBase(path) ? ProjectServerResourceNames.ProjectStateDirectoryBase
            : ProjectServerResourceNames.IsInDeploymentsBase(path) ? ProjectServerResourceNames.DeploymentsBase
            : null;
        if (container is null) return null;
        return IsLink(ProjectServerResourceNames.AgentWorkDirectory) ? ProjectServerResourceNames.AgentWorkDirectory
            : IsLink(container) ? container
            : null;
    }

    private static async Task WriteUnreadableAsync(HostState state, Func<string, TaskLogLevel, Task> onOutput)
    {
        foreach (var (kind, scope, reason) in state.Unreadable)
            await onOutput(ProjectServerResourceOutput.Unreadable(kind, scope, reason), TaskLogLevel.Warning).ConfigureAwait(false);
    }

    private static async Task<ExecutorResult> FailAsync(Func<string, TaskLogLevel, Task> onOutput, string message)
    {
        await onOutput(message, TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(-1, false);
    }
}
