// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers;
namespace Aetheus.Front.Layout;

public partial class ServerDetailLayout : IDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;

    private bool _canWrite;
    private bool _deleteBusy;
    private bool _editVisible;
    private bool _editSaving;
    private string _editName = string.Empty;
    private ServerType _editType;
    private string _editTags = string.Empty;
    private static readonly ServerType[] _serverTypes = Enum.GetValues<ServerType>();

    private AgentUpdateProgressCard? _agentUpdateProgress;

    protected override void OnInitialized()
    {
        Loader.OnChanged += OnLoaderChanged;
        Nav.LocationChanged += OnLocationChanged;
        // MainLayout loads permissions in parallel with route activation, so a section page can
        // mount BEFORE Permissions.IsLoaded - capturing _canWrite=false would strand the
        // Contact/Update/Edit/Delete buttons as permanently disabled. Subscribe so they
        // reactivate the moment permissions land.
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
        // Set the breadcrumb entity name on mount because a loader already holding the entity emits
        // no later change event.
        ReassertBreadcrumb();
    }

    private void OnLoaderChanged()
    {
        ReassertBreadcrumb();
        // The server id is only known once the loader resolves the detail; recompute the
        // resource-scoped permission then.
        RefreshCanWrite();
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        ReassertBreadcrumb();
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>Where the server stands, for OE's detail frame.</summary>
    internal OmniDetailState DetailState => Loader.Server is not null
        ? OmniDetailState.Found
        : Loader.InitialLoadCompleted ? OmniDetailState.NotFound : OmniDetailState.Loading;

    /// <summary>The header's back arrow: the logical parent (R3-29). The server crumb links to the
    /// overview, so on the overview itself the arrow leads to the list instead of to the same page.</summary>
    internal string? BackHref => LogicalParentRoute.Of(Nav.ToBaseRelativePath(Nav.Uri), Breadcrumb.Items);

    /// <summary>/servers/{id} and /servers/{id}/overview show the server's own actions.</summary>
    internal bool IsOverview => IsOverviewPath(Nav.ToBaseRelativePath(Nav.Uri));

    internal static bool IsOverviewPath(string relativePath)
    {
        var segments = relativePath.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 && segments[0] == "servers"
            || segments.Length == 3 && segments[0] == "servers" && segments[2] == "overview";
    }

    /// <summary>The header shows the section's icon, the same one as the side menu.</summary>
    internal string SectionIcon => SectionIconFor(Nav.ToBaseRelativePath(Nav.Uri));

    internal static string SectionIconFor(string relativePath)
    {
        var segments = relativePath.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (segments.Length > 2 ? segments[2] : "overview") switch
        {
            "services" => "engineering",
            "projects" => "folder",
            "pipelines" => "account_tree",
            "libraries" => "library_books",
            "vaults" => "lock",
            "releases" => "new_releases",
            "ports" => "lan",
            "tasks" => "task_alt",
            "logs" => "receipt_long",
            "mail" => "mail",
            "updates" => "system_update",
            _ => "dns",
        };
    }

    private void ReassertBreadcrumb()
    {
        if (Loader.Server is null) return;
        var items = BreadcrumbRouteResolver.Resolve(Nav.ToBaseRelativePath(Nav.Uri), key => L[key]).ToArray();
        if (items.Length > 1)
            items[1] = items[1] with { Text = Loader.Server.Name, IsLoading = false };
        // The section's own version belongs to the title, the last item (R-120 Apache, R-121 Certbot).
        if (items.Length > 2)
            items[^1] = items[^1] with { Text = SectionTitle(Nav.ToBaseRelativePath(Nav.Uri), items[^1].Text, Loader.Server) };
        Breadcrumb.Set(items);
    }

    /// <summary>A section's title with the version of the software it manages ("Apache 2.4.58", "Certbot 5.8.0");
    /// every other section, or one whose server did not report a version, keeps its plain name.</summary>
    internal static string SectionTitle(string relativePath, string title, ServerDetailDto server) =>
        SectionVersion(server, relativePath) is { } version ? $"{title} {version}" : title;

    /// <summary>The version of the software a section manages (Apache, Certbot), or null when the section has none
    /// or the server did not report it.</summary>
    internal static string? SectionVersion(ServerDetailDto server, string relativePath)
    {
        var segments = relativePath.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var version = segments switch
        {
            ["servers", _, "certbot", ..] => server.Certbot?.Version,
            ["servers", _, "apache"] => server.Apache?.Version,
            // The package version ("1.2-14build1"): the title says the upstream release only.
            ["servers", _, "portsentry", ..] => server.Portsentry?.Version?.Split('-')[0],
            _ => null
        };
        return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
    }

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        _ = InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() =>
        _canWrite = Permissions.CanWrite(ResourceType.Server, Loader.Server?.Id ?? 0);

    private Task OpenContactAgentDialog()
    {
        if (Loader.Server is null) return Task.CompletedTask;
        return Dialog.OpenAsync<ContactAgentDialog>(
            L["ContactAgent"],
            new Dictionary<string, object?> { { "ServerId", Loader.Server.Id }, { "ServerName", Loader.Server.Name } },
            new OmniDialogOptions { Width = "480px", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });
    }

    private Task OpenPortCheckDialog()
    {
        if (Loader.Server is null) return Task.CompletedTask;
        return Dialog.OpenAsync<PortCheckDialog>(
            L["CheckPorts"],
            new Dictionary<string, object?> { { "ServerId", Loader.Server.Id } },
            new OmniDialogOptions { Width = "640px", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });
    }

    // The YAML import/export already lives in the Configuration section, complete with its validate and
    // preview steps. The menu entries make it discoverable from the header instead of duplicating it.
    private void GoToConfiguration()
    {
        if (Loader.Server is null) return;
        Nav.NavigateTo($"/servers/{Loader.Server.Id}/configuration");
    }

    private bool CanEnableAgentCapabilities =>
        Loader.Server is { } server && AgentCapabilitiesDialog.IsAvailableFor(server);

    private Task OpenAgentCapabilitiesDialog() =>
        Loader.Server is null ? Task.CompletedTask : AgentCapabilitiesDialog.OpenAsync(Dialog, L, Loader.Server);

    private async Task UpdateAgentAsync()
    {
        if (Loader.Server is null) return;
        await new AgentUpdateRequester(Api, Dialog, Toast, L).RequestAsync(Loader.Server);
    }

    // The progress card heard nothing for a while (an event lost while the tab slept); re-read the
    // persisted request, whose status the card then applies.
    private Task ReloadAgentUpdateAsync() =>
        Loader.Server is null ? Task.CompletedTask : Loader.ReloadAgentUpdateAsync(Loader.Server.Id, CancellationToken.None);

    private async Task OnRetireServer()
    {
        if (Loader.Server is null || _deleteBusy) return;
        var confirmed = await Dialog.Confirm(
            string.Format(L["RetireServerConfirm"], Loader.Server.Name),
            L["RetireServer"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Retire"], CancelButtonText = L["GoBack"] });
        if (confirmed != true) return;

        await Veil.RunAsync(RetireServerConfirmedAsync);
    }

    internal async Task RetireServerConfirmedAsync()
    {
        if (Loader.Server is null || _deleteBusy) return;

        _deleteBusy = true;
        StateHasChanged();
        try
        {
            var retired = await Api.Servers.RetireServerAsync(Loader.Server.Id);
            if (retired)
            {
                Toast.Success(L["ServerRetired"]);
                Nav.NavigateTo("/servers");
            }
            else
            {
                Toast.Error("Error", "ServerRetireFailed");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Toast.Error("Error", "ServerRetireFailed");
        }
        finally
        {
            _deleteBusy = false;
            StateHasChanged();
        }
    }

    private void OpenEditDialog()
    {
        if (Loader.Server is null) return;
        _editName = Loader.Server.Name;
        _editType = Loader.Server.Type;
        _editTags = string.Join(", ", Loader.Server.Tags);
        _editVisible = true;
    }

    private async Task SaveEditAsync()
    {
        if (Loader.Server is null) return;
        _editSaving = true;
        var tags = _editTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var request = new UpdateServerRequest
        {
            Name = _editName,
            Type = _editType,
            Tags = tags
        };
        var updated = await Api.Servers.UpdateServerAsync(Loader.Server.Id, request);
        if (updated is not null)
        {
            Loader.UpdateServerFields(updated.Name, updated.Type, updated.Tags);
            _editVisible = false;
            Toast.Success("Saved", "ServerUpdated");
        }
        else
        {
            Toast.Error("Error", "ServerUpdateFailed");
        }
        _editSaving = false;
    }

    public void Dispose()
    {
        Loader.OnChanged -= OnLoaderChanged;
        Nav.LocationChanged -= OnLocationChanged;
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
    }
}
