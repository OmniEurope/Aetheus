// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Apache;

public class ApacheService(IApacheRepository repo, IAuditService audit, ITaskService taskService) : IApacheService
{
    private Task QueueTaskAsync(ServerTask task, CancellationToken ct = default)
        => TaskQueuePersistence.PersistAndNotifyAsync(repo.AddTaskAsync, taskService, task, ct);

    public async Task<ApacheDataDto> GetStateAsync(int serverId, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false);
        if (state is null)
            return new ApacheDataDto { IsInstalled = false };

        var modules = await GetModulesAsync(serverId, ct).ConfigureAwait(false);
        var vhosts = await GetVirtualHostsAsync(serverId, ct).ConfigureAwait(false);

        return new ApacheDataDto
        {
            IsInstalled = true,
            IsRunning = state.IsRunning,
            Version = state.Version,
            Pid = state.Pid,
            ConfigRoot = state.ConfigRoot,
            CollectionDegraded = state.CollectionDegraded,
            CollectionDiagnostics = state.CollectionDiagnostics,
            Modules = modules,
            VirtualHosts = vhosts
        };
    }

    public async Task<List<ApacheModuleDto>> GetModulesAsync(int serverId, CancellationToken ct = default)
    {
        var modules = await repo.GetModulesAsync(serverId, ct).ConfigureAwait(false);
        return modules.Select(m => new ApacheModuleDto
        {
            Name = m.Name,
            Type = m.Type,
            IsEnabled = m.IsEnabled
        }).ToList();
    }

    public async Task<List<ApacheVirtualHostDto>> GetVirtualHostsAsync(int serverId, CancellationToken ct = default)
    {
        var vhosts = await repo.GetVirtualHostsAsync(serverId, ct).ConfigureAwait(false);
        return vhosts.Select(v => new ApacheVirtualHostDto
        {
            ServerName = v.ServerName,
            Port = v.Port,
            DocumentRoot = v.DocumentRoot,
            ConfigFile = v.ConfigFile,
            IsEnabled = v.IsEnabled
        }).ToList();
    }

    public async Task ExecuteActionAsync(int serverId, ApacheActionRequest request, CancellationToken ct = default)
    {
        await QueueTaskAsync(BuildActionTask(serverId, request), ct).ConfigureAwait(false);
        await audit.LogAsync($"Apache{request.Action}", "Apache", serverId,
            request.TargetName ?? request.Action.ToString(), ct).ConfigureAwait(false);
    }

    // Items #5.2/#5.3/#6: service-control actions go through typed operations (sudo + argv exact via the
    // controlled-sudo recipe). Recette R-12: enabling, disabling and deleting a site are typed operations
    // too - the `a2ensite`/`a2dissite` shell strings they used to send were refused by the agent's
    // CommandValidator. Only the module actions are still shell strings, and the allow-list refuses them as
    // well (no typed operation exists for mods-enabled yet).
    private static ServerTask BuildActionTask(int serverId, ApacheActionRequest request)
    {
        if (ServiceOperation(request.Action) is { } op)
            return ServerTaskFactory.Operation(serverId, $"Apache {request.Action}", op, target: "-", timeoutSeconds: 30);

        if (SiteOperation(request.Action) is { } siteKind)
        {
            var siteFile = ValidSiteFile(request);
            // A configtest, a reload and, on failure, their rollback: more than one 30 s step.
            return ServerTaskFactory.Operation(serverId, $"Apache {request.Action} - {siteFile}",
                siteKind, target: siteFile, timeoutSeconds: 90);
        }

        var command = request.Action switch
        {
            ApacheAction.EnableModule => BuildModuleCommand(request, ApacheCommandHelper.BuildEnableModuleCommand),
            ApacheAction.DisableModule => BuildModuleCommand(request, ApacheCommandHelper.BuildDisableModuleCommand),
            _ => throw new BadRequestException($"Unknown Apache action: {request.Action}")
        };
        return ServerTaskFactory.Shell(serverId,
            $"Apache {request.Action}{(request.TargetName is not null ? $" - {request.TargetName}" : "")}",
            command, 30);
    }

    private static OperationKind? ServiceOperation(ApacheAction action) => action switch
    {
        ApacheAction.Reload => OperationKind.ApacheReload,
        ApacheAction.TestConfig => OperationKind.ApacheTestConfig,
        ApacheAction.Start => OperationKind.ApacheStart,
        ApacheAction.Stop => OperationKind.ApacheStop,
        ApacheAction.Restart => OperationKind.ApacheRestart,
        _ => null
    };

    private static OperationKind? SiteOperation(ApacheAction action) => action switch
    {
        ApacheAction.EnableSite => OperationKind.ApacheEnableSite,
        ApacheAction.DisableSite => OperationKind.ApacheDisableSite,
        ApacheAction.DeleteSite => OperationKind.ApacheDeleteSite,
        _ => null
    };

    public async Task GetLogsAsync(int serverId, ApacheLogRequest request, CancellationToken ct = default)
    {
        // Item #8: switched from a shell `tail … 2>/dev/null || tail …` (rejected by the
        // CommandValidator since the RKHunter hardening) to a typed operation. The agent reads
        // the log file with `tail` argv-only and resolves the platform path with File.Exists.
        var logType = request.LogType is "access" ? "access" : "error";
        var task = ServerTaskFactory.Operation(
            serverId,
            $"Apache logs - {logType}",
            OperationKind.ApacheGetLogs,
            target: logType,
            timeoutSeconds: 15);
        await QueueTaskAsync(task, ct).ConfigureAwait(false);
    }

    public async Task GetVHostConfigAsync(int serverId, string siteName, CancellationToken ct = default)
    {
        if (!ApacheCommandHelper.IsValidSiteName(siteName))
            throw new BadRequestException("Invalid site name.");

        // Switched from a shell `cat "..." 2>/dev/null || cat "..."` (rejected by the CommandValidator)
        // to a typed read: the agent reads the vhost file directly (read ACL, no shell). The config root
        // travels in env so the agent resolves the right sites-available/conf.d location.
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false);
        var configRoot = state?.ConfigRoot ?? "/etc/apache2";
        var task = ServerTaskFactory.Operation(
            serverId, $"Apache config - {siteName}", OperationKind.ApacheGetConfig, target: siteName,
            environmentVariables: new Dictionary<string, string> { ["AETHEUS_APACHE_CONFIG_ROOT"] = configRoot },
            timeoutSeconds: 10);
        await QueueTaskAsync(task, ct).ConfigureAwait(false);
    }

    public async Task SaveVHostConfigAsync(int serverId, ApacheVHostSaveRequest request, CancellationToken ct = default)
    {
        if (!ApacheCommandHelper.IsValidSiteName(request.SiteName))
            throw new BadRequestException("Invalid site name.");

        // Item #5.3: switched from a shell `printf '%s' '<b64>' | base64 -d > path` (rejected
        // by the CommandValidator) to a typed operation. Agent writes via File.WriteAllBytes
        // through the ACL +rw granted by --enable-apache-manage - no sudo, no shell.
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.Content));
        var task = new ServerTask
        {
            ServerId = serverId,
            Name = $"Apache save config - {request.SiteName}",
            Command = request.SiteName,
            Executor = ExecutorType.Operation,
            Operation = OperationKind.ApacheSaveConfig,
            Status = TaskExecutionStatus.Pending,
            TimeoutSeconds = 10,
            EnvironmentVariables = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string> { ["AETHEUS_APACHE_CONFIG_B64"] = b64 })
        };
        await QueueTaskAsync(task, ct).ConfigureAwait(false);

        await audit.LogAsync("ApacheSaveConfig", "Apache", serverId, request.SiteName, ct).ConfigureAwait(false);
    }

    public async Task GetHtaccessAsync(int serverId, string documentRoot, CancellationToken ct = default)
    {
        if (!ApacheCommandHelper.IsValidDocumentRoot(documentRoot))
            throw new BadRequestException("Invalid document root path.");

        // Switched from a shell `cat "..." || echo ''` (rejected by the CommandValidator) to a typed read:
        // the agent reads {documentRoot}/.htaccess directly (empty when absent).
        var task = ServerTaskFactory.Operation(
            serverId, $"Apache htaccess - {documentRoot}", OperationKind.ApacheGetHtaccess,
            target: documentRoot, timeoutSeconds: 10);
        await QueueTaskAsync(task, ct).ConfigureAwait(false);
    }

    public async Task SaveHtaccessAsync(int serverId, ApacheHtaccessSaveRequest request, CancellationToken ct = default)
    {
        if (!ApacheCommandHelper.IsValidDocumentRoot(request.DocumentRoot))
            throw new BadRequestException("Invalid document root path.");

        // Switched from a shell `printf '%s' '<b64>' | base64 -d > path` (rejected by the CommandValidator)
        // to a typed write: the agent writes {documentRoot}/.htaccess via File.WriteAllBytes (no shell).
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.Content));
        var task = ServerTaskFactory.Operation(
            serverId, $"Apache save htaccess - {request.DocumentRoot}", OperationKind.ApacheSaveHtaccess,
            target: request.DocumentRoot,
            environmentVariables: new Dictionary<string, string> { ["AETHEUS_APACHE_HTACCESS_B64"] = b64 },
            timeoutSeconds: 10);
        await QueueTaskAsync(task, ct).ConfigureAwait(false);

        await audit.LogAsync("ApacheSaveHtaccess", "Apache", serverId, request.DocumentRoot, ct).ConfigureAwait(false);
    }

    // The agent acts on sites-available/<file> and sites-enabled/<file>: a bare ".conf" file name, no path
    // separator, no leading dot (the shared contract the agent checks again before touching the disk).
    private static string ValidSiteFile(ApacheActionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TargetName))
            throw new BadRequestException("Site name is required.");
        if (!OperationTargetValidator.ApacheSiteFileRegex().IsMatch(request.TargetName))
            throw new BadRequestException("Invalid site name.");
        return request.TargetName;
    }

    private static string BuildModuleCommand(ApacheActionRequest request, Func<string, string> commandBuilder)
    {
        if (string.IsNullOrWhiteSpace(request.TargetName))
            throw new BadRequestException("Module name is required.");
        if (!ApacheCommandHelper.IsValidModuleName(request.TargetName))
            throw new BadRequestException("Invalid module name.");
        return commandBuilder(request.TargetName);
    }
}
