// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Coordinates Docker tabs, shared prune confirmation and component lifetime, forwarding
/// task output to the waiting tab.</summary>
public partial class ServerDockerSection : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;
    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter] public bool DockerInitialLoaded { get; set; }
    [Parameter] public EventCallback<ServerDetailDto> ServerChanged { get; set; }

    private readonly CancellationTokenSource _lifetimeCts = new();
    private int? _activeServerId;
    private bool _disposed;

    private bool _pruning;
    private ServerTaskFollower? _follower;

    private bool _dockerInitialLoaded;

    // The two tabs that consume task output of their own; the parent keeps a handle on each to forward.
    private DockerContainersTab? _containersTab;
    private DockerComposeTab? _composeTab;

    /// <summary>
    /// BACK-012: compose deploy, image build, container shell (and, before them, image delete and prune)
    /// take Server.Admin on the backend; a user without it is not offered them. Permissions may land after
    /// this section mounts, hence the subscription.
    /// </summary>
    private bool CanAdmin => Permissions.CanAdmin(ResourceType.Server, ServerId);

    protected override void OnInitialized() => Permissions.OnPermissionsChanged += OnPermissionsChanged;

    private void OnPermissionsChanged() => InvokeAsync(StateHasChanged);

    protected override void OnParametersSet()
    {
        if (_activeServerId != ServerId)
        {
            _activeServerId = ServerId;
            // Per-tab state is not reset here: every tab carries @key="ServerId" and is rebuilt instead.
            _dockerInitialLoaded = DockerInitialLoaded;
        }
        else if (DockerInitialLoaded)
            _dockerInitialLoaded = true;
    }

    public void HandleTaskCompleted(TaskCompletedNotification notification)
    {
        if (notification.ServerId != ServerId) return;
        // Recette R3-34: the notification carries no output, so the text a tab waits for is read from the
        // task's log; a failed task still reaches the tab, flagged, so it can say why.
        if (notification.TaskName.StartsWith("Docker", StringComparison.OrdinalIgnoreCase))
            _ = InvokeAsync(() => DispatchTaskOutputAsync(notification));

        // HTC5: HandleTaskCompleted is invoked from the SignalR callback (outside the render loop),
        // so marshal the re-render back onto the renderer's sync context - as the Apache/Mail/Services
        // sections already do - otherwise the real-time update silently never paints.
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Hands the raw task output to every tab that consumes any; each decides whether it was waiting
    /// for this one. The parent deliberately knows nothing about task names.
    /// </summary>
    private async Task DispatchTaskOutputAsync(TaskCompletedNotification notification)
    {
        string output;
        try { output = await ServerTaskOutputLoader.ReadAsync(Api, notification.TaskId); }
        catch (HttpRequestException) { output = L["TaskOutputUnavailable"]; }
        var failed = notification.Status != TaskExecutionStatus.Success;
        // A successful task that wrote nothing has nothing to show: the tabs keep what they display.
        if (!failed && string.IsNullOrEmpty(output)) return;
        _containersTab?.HandleTaskOutput(notification.TaskName, output);
        _composeTab?.HandleTaskOutput(notification.TaskName, output, failed, notification.TaskId);
        StateHasChanged();
    }

    // SITE-OFFLINE-001: a prune is an agent task.
    private bool AgentOffline => AgentOfflineGuard.IsOffline(Server);

    private async Task OpenPruneDialogAsync()
    {
        if (AgentOfflineGuard.Blocks(Server, Toast)) return;
        var result = await Dialog.OpenAsync<DockerPruneDialog>(
            L["DockerPruneDialog"].Value,
            options: new OmniDialogOptions { Width = "32rem", AutoFocusFirstElement = false });
        if (result is DockerPruneDialogResult selection)
            await PruneAsync(selection.Containers, selection.Images, selection.Volumes);
    }

    private async Task PruneAsync(bool containers, bool images, bool volumes)
    {
        _pruning = true;
        // Recette R3-37: the follower shows what becomes of the task, in place of a lone "queued" toast.
        _follower?.Expect(L["DockerPrune"]);
        // finally, not a trailing assignment: PruneDockerAsync reaches the network without catching, so
        // an HttpRequestException used to leave the flag stuck at true and the button disabled until the
        // component was rebuilt.
        try
        {
            var request = new DockerPruneRequest { Containers = containers, Images = images, Volumes = volumes };
            var success = await Api.ServerTools.PruneDockerAsync(ServerId, request);
            if (success)
            {
                Toast.Success("DockerPrune", "DockerPruneQueued");
            }
            else
            {
                _follower?.Forget();
                Toast.Error("DockerPrune", "DockerPruneFailed");
            }
        }
        catch (HttpRequestException)
        {
            _follower?.Forget();
            throw;
        }
        finally
        {
            _pruning = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        await _lifetimeCts.CancelAsync();
        _lifetimeCts.Dispose();
    }
}
