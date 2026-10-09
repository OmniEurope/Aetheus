// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Microsoft.AspNetCore.Components.Web;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// The Docker containers tab: the grid, the per-container and per-project actions, and the five
/// detail panels (logs, inspect, shell, environment, file browser).
///
/// It is the largest tab and the only one with four inbound task channels, so the parent holds an
/// <c>@ref</c> to it and forwards raw task output to <see cref="HandleTaskOutput"/>. Everything it
/// owns is either view state or a request in flight; the container inventory itself belongs to the
/// parent, which refreshes it and passes it down through <see cref="Server"/>. The parent's
/// <c>@key="ServerId"</c> is what clears the state when the server changes.
/// </summary>
public partial class DockerContainersTab
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>
    /// The whole server, not just its containers: the project zoom dialog takes the server, and the
    /// resource-limits dialog seeds itself from the container's current limit. Never mutated here.
    /// </summary>
    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>BACK-012: the backend takes Server.Admin for this tab's agent-reaching writes (compose
    /// deploy, image build and delete, container shell); without it they are not offered.</summary>
    [Parameter] public bool CanAdmin { get; set; }

    /// <summary>False until the parent's first inventory load lands; drives the skeleton rows.</summary>
    [Parameter] public bool InitialLoaded { get; set; }

    private string _containerSearch = string.Empty;

    // Recette R-227: each heartbeat brings a new container list; containers that were not there read bold.
    private OmniDataGrid<DockerContainerDto>? _grid;
    private readonly LiveGridRows _liveRows = new();

    protected override Task OnParametersSetAsync() => _liveRows.ObserveAsync(_grid, Server.Docker.Containers);
    private string? _dockerActionTarget;
    private string? _projectActionTarget;

    private string? _logsContainerId;
    private string? _logsContent;

    private string? _inspectContainerId;
    private string? _inspectContent;

    private string? _shellContainerId;
    private string _shellContainerName = string.Empty;
    private string _shellCommand = string.Empty;
    private string _shellOutput = string.Empty;

    private string? _envContainerId;
    private string _envContainerName = string.Empty;
    private string? _envContent;

    private string? _browseContainerId;
    private string _browseContainerName = string.Empty;
    private string _browsePath = "/";
    private string? _browseContent;

    private IReadOnlyList<OmniDataGridGroup> _containerGroups =
    [
        new(nameof(DockerContainerDto.Project), false)
    ];

    // Open the inventory on its rows rather than collapsing its project groups.
    private bool _allContainerGroupsExpanded = true;
    private string? _bulkProject;

    private IReadOnlyList<OmniOption<string>> BulkProjectOptions => FilteredContainers
        .Select(container => container.Project ?? string.Empty)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(project => project, StringComparer.Ordinal)
        .Select(project => new OmniOption<string>(project,
            string.IsNullOrEmpty(project) ? L["NoProject"].Value : project))
        .ToList();

    private string? SelectedBulkProject => _bulkProject is not null
        && BulkProjectOptions.Any(option => string.Equals(option.Value, _bulkProject, StringComparison.Ordinal))
            ? _bulkProject
            : null;

    private string ContainerGroupLabel(object? value, int count) =>
        $"{(string.IsNullOrEmpty(value?.ToString()) ? L["NoProject"].Value : value)} ({count})";

    private List<DockerContainerDto> FilteredContainers => Server.Docker.Containers
        .Where(c => string.IsNullOrWhiteSpace(_containerSearch)
            || c.Name.Contains(_containerSearch, StringComparison.OrdinalIgnoreCase)
            || c.Image.Contains(_containerSearch, StringComparison.OrdinalIgnoreCase)
            || c.State.Contains(_containerSearch, StringComparison.OrdinalIgnoreCase))
        .ToList();

    /// <summary>
    /// Consumes one completed agent task forwarded by the parent, and repaints only if it was one of
    /// this tab's four channels. The parent calls this from the SignalR callback, outside the render
    /// loop, so the repaint is marshalled back onto the renderer's sync context.
    /// </summary>
    public void HandleTaskOutput(string taskName, string output)
    {
        var consumed = false;

        if (_inspectContainerId is not null && taskName.Contains("inspect"))
        {
            _inspectContent = output;
            consumed = true;
        }

        if (_shellContainerId is not null && taskName.Contains("exec"))
        {
            _shellOutput += output + "\n";
            consumed = true;
            _ = JS.InvokeVoidAsync("dockerInterop.scrollToBottom", "docker-shell-output");
        }

        if (_envContainerId is not null && taskName.Contains("env"))
        {
            _envContent = output;
            consumed = true;
        }

        if (_browseContainerId is not null && taskName.Contains("ls"))
        {
            _browseContent = output;
            consumed = true;
        }

        if (consumed) _ = InvokeAsync(StateHasChanged);
    }

    private async Task DockerActionAsync(string containerId, DockerContainerAction action)
    {
        _dockerActionTarget = containerId;
        var request = new DockerActionRequest { ContainerId = containerId, Action = action };
        var success = await Api.ServerTools.ExecuteDockerActionAsync(ServerId, request);
        if (success)
        {
            Toast.Success("Docker", "DockerActionSent", action, containerId[..Math.Min(12, containerId.Length)]);
        }
        else
        {
            Toast.Error("Docker", "DockerActionFailed", action);
        }
        _dockerActionTarget = null;
    }

    private List<DockerContainerDto> ContainersForProject(string project) =>
        Server.Docker.Containers
            .Where(c => string.Equals(c.Project ?? string.Empty, project ?? string.Empty, StringComparison.Ordinal))
            .ToList();

    private async Task ProjectBulkActionAsync(string project, DockerContainerAction action)
    {
        var targets = ContainersForProject(project);
        if (targets.Count == 0) return;

        _projectActionTarget = project;
        var ok = 0;
        foreach (var c in targets)
        {
            var req = new DockerActionRequest { ContainerId = c.ContainerId, Action = action };
            if (await Api.ServerTools.ExecuteDockerActionAsync(ServerId, req)) ok++;
        }
        _projectActionTarget = null;

        if (ok == targets.Count)
            Toast.Success("Docker", "DockerActionSent", action, project);
        else
            Toast.Error("Docker", "DockerActionFailed", $"{action} ({ok}/{targets.Count})");
    }

    private async Task OpenProjectZoom(string project)
    {
        var result = await Dialog.OpenAsync<DockerProjectDialog>(
            $"{L["ZoomProject"]}: {(string.IsNullOrEmpty(project) ? L["NoProject"] : project)}",
            new Dictionary<string, object?> { { "Server", Server }, { "Project", project } },
            new OmniDialogOptions { Width = "90vw", Height = "85vh", Resizable = true, Draggable = true, AutoFocusFirstElement = false });

        if (result is DockerContainerAction action)
            await ProjectBulkActionAsync(project, action);
    }

    private async Task ConfirmRemoveContainerAsync(string containerId, string name)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["RemoveContainerConfirm"].Value, name),
            L["RemoveContainer"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed == true)
            await DockerActionAsync(containerId, DockerContainerAction.Remove);
    }

    private async Task ToggleLogsAsync(string containerId)
    {
        if (_logsContainerId == containerId)
        {
            _logsContainerId = null;
            _logsContent = null;
            return;
        }

        _logsContainerId = containerId;
        _logsContent = null;
        var request = new DockerContainerLogsRequest { ContainerId = containerId, Tail = 100 };
        _logsContent = await Api.ServerTools.GetContainerLogsAsync(ServerId, request);
    }

    private async Task OpenResourceLimitsDialog(string containerId)
    {
        var container = Server.Docker.Containers.FirstOrDefault(c => c.ContainerId == containerId);
        var result = await Dialog.OpenAsync<DockerResourceLimitsDialog>(
            L["ResourceLimits"].Value,
            new Dictionary<string, object?>
            {
                { "ContainerId", containerId },
                { "CpuLimit", 0d },
                { "MemoryLimitMb", (int)(container?.MemoryLimitMb ?? 0) }
            },
            new OmniDialogOptions { Width = "32rem", AutoFocusFirstElement = false });
        if (result is not DockerResourceLimitsDialogResult limits) return;

        var request = new DockerResourceLimitsRequest
        {
            ContainerId = containerId,
            CpuLimit = limits.CpuLimit,
            MemoryLimitMb = limits.MemoryLimitMb
        };
        var success = await Api.ServerTools.UpdateDockerResourceLimitsAsync(ServerId, request);
        if (success)
        {
            Toast.Success("Docker", "ResourceLimitsQueued");
        }
        else
        {
            Toast.Error("Docker", "ResourceLimitsFailed");
        }
    }

    private void OnContainerRowRender(OmniDataGridRowRenderArgs<DockerContainerDto> args)
    {
        var stateClass = args.Item.State switch
        {
            "running" => "docker-row-running",
            "exited" => "docker-row-exited",
            "paused" => "docker-row-paused",
            _ => "docker-row-other"
        };
        var projectClass = GetProjectAccentClass(args.Item.Project);
        args.Class = string.IsNullOrEmpty(projectClass)
            ? stateClass
            : $"{stateClass} docker-row-project {projectClass}";
    }

    internal static string GetProjectAccentClass(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return string.Empty;
        var hash = 0;
        foreach (var c in project) hash = unchecked(hash * 31 + c);
        return $"docker-project-accent-{Math.Abs(hash) % 8}";
    }

    // internal, not private: these three are pure functions with no UI trigger, so the tests used to
    // reach them by reflection (A360-10). InternalsVisibleTo makes that unnecessary.
    internal static OmniTone GetContainerBadge(string state) => state switch
    {
        "running" => OmniTone.Success,
        "exited" => OmniTone.Danger,
        "paused" => OmniTone.Warning,
        "restarting" => OmniTone.Accent,
        _ => OmniTone.Neutral
    };

    internal static string GetMemoryBarClass(DockerContainerDto c)
    {
        if (c.MemoryLimitMb <= 0) return "docker-mem-bar";
        var pct = c.MemoryUsageMb / c.MemoryLimitMb * 100;
        return pct switch
        {
            >= 90 => "docker-mem-bar docker-mem-red",
            >= 70 => "docker-mem-bar docker-mem-yellow",
            _ => "docker-mem-bar docker-mem-green"
        };
    }

    private async Task RequestInspectAsync(string containerId)
    {
        if (_inspectContainerId == containerId)
        {
            _inspectContainerId = null;
            _inspectContent = null;
            return;
        }

        _inspectContainerId = containerId;
        _inspectContent = null;
        var success = await Api.ServerTools.InspectContainerAsync(ServerId, containerId);
        if (!success)
        {
            Toast.Error("Docker", "DockerInspectFailed");
            _inspectContainerId = null;
        }
    }

    private void OpenShell(string containerId, string name)
    {
        if (_shellContainerId == containerId)
        {
            _shellContainerId = null;
            _shellOutput = string.Empty;
            return;
        }

        _shellContainerId = containerId;
        _shellContainerName = name;
        _shellCommand = string.Empty;
        _shellOutput = string.Empty;
    }

    private async Task SendShellCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(_shellCommand) || _shellContainerId is null) return;
        _shellOutput += $"$ {_shellCommand}\n";
        var request = new DockerExecRequest { ContainerId = _shellContainerId, Command = _shellCommand };
        var success = await Api.ServerTools.ExecuteShellCommandAsync(ServerId, request);
        if (success)
        {
            Toast.Success("Docker", "TaskCreated");
        }
        else
        {
            _shellOutput += L["DockerExecFailed"].Value + "\n";
            Toast.Error("Docker", "DockerExecFailed");
        }
        _shellCommand = string.Empty;
    }

    private async Task OnShellKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await SendShellCommandAsync();
    }

    private async Task RequestEnvVarsAsync(string containerId, string name)
    {
        if (_envContainerId == containerId)
        {
            _envContainerId = null;
            _envContent = null;
            return;
        }

        _envContainerId = containerId;
        _envContainerName = name;
        _envContent = null;
        var success = await Api.ServerTools.GetContainerEnvVarsAsync(ServerId, containerId);
        if (!success)
        {
            Toast.Error("Docker", "DockerEnvVarsFailed");
            _envContainerId = null;
        }
    }

    internal static List<DockerEnvVarDto> ParseEnvVars(string json)
    {
        var result = new List<DockerEnvVarDto>();

        // Docker reports a container's env as a JSON array of "KEY=VALUE" strings,
        // where VALUE may itself contain '=' and ',' (PATH, connection strings, base64).
        // Deserialize the array with System.Text.Json and split each entry on the
        // FIRST '=' only so those values survive intact.
        string[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return result;
        }

        if (entries is null) return result;

        foreach (var entry in entries)
        {
            if (entry is null) continue;
            var eqIndex = entry.IndexOf('=');
            if (eqIndex > 0)
            {
                result.Add(new DockerEnvVarDto
                {
                    Key = entry[..eqIndex],
                    Value = entry[(eqIndex + 1)..]
                });
            }
        }
        return result;
    }

    private async Task OpenFileBrowserAsync(string containerId, string name)
    {
        if (_browseContainerId == containerId)
        {
            _browseContainerId = null;
            _browseContent = null;
            return;
        }

        _browseContainerId = containerId;
        _browseContainerName = name;
        _browsePath = "/";
        _browseContent = null;
        await BrowsePathAsync();
    }

    private async Task BrowsePathAsync()
    {
        _browseContent = null;
        var request = new DockerBrowseRequest { ContainerId = _browseContainerId!, Path = _browsePath };
        var success = await Api.ServerTools.ListContainerFilesAsync(ServerId, request);
        if (!success)
        {
            Toast.Error("Docker", "DockerBrowseFailed");
            _browseContainerId = null;
        }
    }

    private async Task BrowseParentAsync()
    {
        if (_browsePath == "/") return;
        var lastSlash = _browsePath.TrimEnd('/').LastIndexOf('/');
        _browsePath = lastSlash <= 0 ? "/" : _browsePath[..lastSlash];
        await BrowsePathAsync();
    }
}
