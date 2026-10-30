// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Routing;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Layout;

public partial class NavMenu : IDisposable
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private AlertNotificationService AlertNotify { get; set; } = default!;
    [Inject] private UserNotificationService UserNotify { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private AppDialogs Dialog { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private ServerDetailLoader ServerLoader { get; set; } = default!;


    /// <summary>A page of the Administration section: every one for a site Admin, those its permissions open
    /// for an organization's administrator (owner decision 2026-10-28).</summary>
    private bool ShowsAdminPage(string href) => Auth.IsAdmin || OrganizationAdministration.CanOpen(href, Permissions);

    private string _currentPath = string.Empty;
    private bool _permDialogOpen;

    protected override async Task OnInitializedAsync()
    {
        _currentPath = GetRelativePath();
        Nav.LocationChanged += OnLocationChanged;
        AlertNotify.OnChange += OnAlertCountChanged;
        AlertNotify.OnServerOffline += OnServerOffline;
        UserNotify.OnPermissionsChanged += OnPermissionsChanged;
        Permissions.OnPermissionsChanged += OnEffectivePermissionsChanged;
        ProjectNav.OnChanged += OnProjectCtxChanged;
        ServerLoader.OnChanged += OnServerLoaderChanged;
        await AlertNotify.StartAsync();
        await UserNotify.StartAsync();
    }

    // An admin changed this user's roles/orgs/role-permissions: block the UI with a non-dismissable
    // dialog whose only action is a full reload (re-mints the token via refresh and re-fetches
    // permissions). Guarded so a burst that slips past the service debounce can't stack dialogs.
    private void OnPermissionsChanged()
    {
        _ = InvokeAsync(async () =>
        {
            if (_permDialogOpen) return;
            _permDialogOpen = true;
            try
            {
                await Dialog.OpenAsync<PermissionsChangedDialog>(
                    L["PermissionsChangedTitle"],
                    null,
                    // STD-DIALOG exception: a permissions change invalidates the current UI contract;
                    // the user must acknowledge it and reload instead of dismissing the warning.
                    new AppDialogOptions { Width = AppDialogWidth.Narrow, Dismissible = false });
            }
            finally
            {
                _permDialogOpen = false;
            }
        });
    }

    private void OnServerOffline(ServerOfflineNotificationPayload payload)
    {
        InvokeAsync(() =>
        {
            Toast.Warning("ServerOfflineToastTitle", OfflineToastMessageKey(payload.Reachability), payload.ServerName);
            return Task.CompletedTask;
        });
    }

    /// <summary>PLAN-010 lot 1: the offline alert says whether only the agent or the whole host stopped,
    /// from the backend's external probe; an older backend (no probe) keeps the plain message.</summary>
    internal static string OfflineToastMessageKey(ServerReachability reachability) => reachability switch
    {
        ServerReachability.HostReachable => "ServerOfflineToastMessage_HostReachable",
        ServerReachability.HostUnreachable => "ServerOfflineToastMessage_HostUnreachable",
        ServerReachability.NotProbed => "ServerOfflineToastMessage",
        _ => "ServerOfflineToastMessage_Unknown"
    };

    private void OnProjectCtxChanged() => InvokeAsync(StateHasChanged);

    // The server loader refreshes on every heartbeat, which can install or remove the mail stack.
    private void OnServerLoaderChanged() => InvokeAsync(StateHasChanged);

    /// <summary>The mail entry of a server's menu: the open server has a mail stack (R1-28).</summary>
    private bool HasServerMail(int serverId) => ServerLoader.Server?.Id == serverId && ServerLoader.HasMail;

    private void OnEffectivePermissionsChanged() => InvokeAsync(StateHasChanged);

    private static readonly Dictionary<string, string[]> SectionPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["servers"] = ["servers", "tasks", "logs", "alerts", "add-agent"],
        // git-repositories?projectId=N is a project sub-page even though the URL
        // lives under the top-level git-repositories route. Keeping it in the
        // projects section preserves the submenu when navigating from a project
        // tile to its Git tab.
        ["projects"] = ["projects", "pipelines", "analysis", "monitoring", "backups", "templates", "releases", "variable-libraries", "vaults", "environments", "git-repositories", "artifacts", "service-connections", "ai-tasks"],
        ["settings"] = ["settings"],
        ["admin"] = ["admin", "users", "audit", "plugins", "dashboards", "api-reference"],
    };

    private bool IsOnSection(string section)
    {
        if (!SectionPaths.TryGetValue(section, out var paths))
            return _currentPath.StartsWith(section, StringComparison.OrdinalIgnoreCase);

        foreach (var p in paths)
        {
            if (_currentPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool CanReadProjectArea() =>
        Permissions.CanReadAny(ResourceType.Project)
        || Permissions.CanReadAny(ResourceType.Pipeline)
        || Permissions.CanReadAny(ResourceType.PipelineTemplate)
        || Permissions.CanReadAny(ResourceType.Release)
        || Permissions.CanReadAny(ResourceType.VariableLibrary)
        || Permissions.CanReadAny(ResourceType.Vault)
        || Permissions.CanReadAny(ResourceType.Environment)
        || Permissions.CanReadAny(ResourceType.ServiceConnection);

    private static readonly System.Text.RegularExpressions.Regex ProjectIdRegex = new(@"^projects/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex ServerIdRegex = new(@"^servers/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex GitRepoProjectIdRegex = new(@"^git-repositories(?:/\d+)?\?(?:[^&]*&)*projectId=(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private int? GetSpecificProjectId()
    {
        var match = ProjectIdRegex.Match(_currentPath);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
            return id;

        // Also recognize the project-scoped git-repositories page so the project
        // submenu stays open and points to the right project context.
        var gitMatch = GitRepoProjectIdRegex.Match(_currentPath);
        if (gitMatch.Success && int.TryParse(gitMatch.Groups[1].Value, out var gitId))
            return gitId;

        // Fallback: project-scoped detail pages (vaults/{id}, releases/{id}, pipelines/{id},
        // variable-libraries/{id}) publish the parent ProjectId via ProjectNavContextService
        // so the project submenu stays open while editing one of its resources.
        return ProjectNav.ProjectId;
    }

    private int? GetSpecificServerId()
    {
        var match = ServerIdRegex.Match(_currentPath);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    private string GetRelativePath() =>
        Nav.ToBaseRelativePath(Nav.Uri).TrimStart('/');

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        _currentPath = GetRelativePath();
        if (_currentPath.StartsWith("alerts", StringComparison.OrdinalIgnoreCase))
            AlertNotify.ClearUnread();
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnAlertCountChanged() => InvokeAsync(StateHasChanged);

    private static RenderFragment IconFor(string material) => builder =>
    {
        builder.OpenComponent<OmniIcon>(0);
        builder.AddComponentParameter(1, nameof(OmniIcon.Name), MaterialIconMap.Resolve(material));
        builder.CloseComponent();
    };

    public void Dispose()
    {
        Nav.LocationChanged -= OnLocationChanged;
        AlertNotify.OnChange -= OnAlertCountChanged;
        AlertNotify.OnServerOffline -= OnServerOffline;
        UserNotify.OnPermissionsChanged -= OnPermissionsChanged;
        Permissions.OnPermissionsChanged -= OnEffectivePermissionsChanged;
        ProjectNav.OnChanged -= OnProjectCtxChanged;
        ServerLoader.OnChanged -= OnServerLoaderChanged;
    }
}
