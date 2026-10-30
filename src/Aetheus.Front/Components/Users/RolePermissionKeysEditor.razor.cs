// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Users;

/// <summary>
/// R-11: the named permissions a role holds on its whole organization, one check box list per area of
/// <see cref="PermissionCatalog"/>. A built-in role marks its default permissions; the site Admin holds every
/// permission and is shown read-only. Saving replaces the role-wide grants (a level key also sets the
/// matching Read, Write or Admin level of the matrix tab).
/// </summary>
public partial class RolePermissionKeysEditor
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    [Parameter, EditorRequired] public int RoleId { get; set; }
    [Parameter, EditorRequired] public string RoleName { get; set; } = string.Empty;

    /// <summary>The keys the role holds, as the backend lists them (level keys with their implied lower levels).</summary>
    [Parameter, EditorRequired] public IReadOnlyList<string> Keys { get; set; } = [];

    /// <summary>Raised after a save, so the page reloads what the save changed (the level matrix).</summary>
    [Parameter] public EventCallback Saved { get; set; }

    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private IReadOnlyList<string>? _loadedKeys;
    private IReadOnlyList<string> _expression = [];
    private bool _saving;
    private bool _dirty;

    /// <summary>Shown, not changed: a built-in role read by an organization's administrator (owner decision 2026-10-28).</summary>
    [Parameter] public bool ReadOnly { get; set; }

    private bool IsSiteAdmin => string.Equals(RoleName, RoleNames.Admin, StringComparison.Ordinal);
    private bool Locked => IsSiteAdmin || ReadOnly;
    private bool IsBuiltIn => RoleNames.BuiltIn.Contains(RoleName, StringComparer.Ordinal);

    private string Introduction => IsSiteAdmin ? L["PermissionKeysAdminIntro"] : L["PermissionKeysIntro"];

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(_loadedKeys, Keys)) return;
        _loadedKeys = Keys;
        _selected.Clear();
        _selected.UnionWith(IsSiteAdmin ? PermissionCatalog.All.Select(definition => definition.Key) : Keys);
        _dirty = false;
    }

    internal IReadOnlyList<OmniOption<string>> OptionsOf(string area) =>
        PermissionCatalog.All
            .Where(definition => definition.Area == area)
            .Select(definition => new OmniOption<string>(definition.Key, Label(definition)))
            .ToList();

    internal IReadOnlyList<string> SelectedIn(string area) =>
        PermissionCatalog.All
            .Where(definition => definition.Area == area && _selected.Contains(definition.Key))
            .Select(definition => definition.Key)
            .ToList();

    private string Label(PermissionDefinition definition)
    {
        var label = L[definition.LabelKey].Value;
        return IsBuiltIn && !IsSiteAdmin && definition.DefaultRoles.Contains(RoleName, StringComparer.Ordinal)
            ? $"{label} ({L["PermissionDefault"].Value})"
            : label;
    }

    internal void OnAreaChanged(string area, IReadOnlyList<string>? selected)
    {
        var chosen = selected ?? [];
        foreach (var definition in PermissionCatalog.All.Where(definition => definition.Area == area))
        {
            if (chosen.Contains(definition.Key, StringComparer.Ordinal)) _selected.Add(definition.Key);
            else _selected.Remove(definition.Key);
        }
        _dirty = true;
    }

    private async Task SaveAsync()
    {
        if (_saving || Locked) return;
        _saving = true;
        try
        {
            var status = await Api.Auth.SetRolePermissionKeysAsync(RoleId, new SetRolePermissionKeysRequest
            {
                Keys = PermissionCatalog.All.Select(definition => definition.Key).Where(_selected.Contains).ToList()
            });
            if (status)
            {
                _dirty = false;
                Toast.Success("PermissionsSaved");
                await Saved.InvokeAsync();
            }
            else
            {
                Toast.Error("Error", "SaveFailed");
            }
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _saving = false;
        }
    }
}
