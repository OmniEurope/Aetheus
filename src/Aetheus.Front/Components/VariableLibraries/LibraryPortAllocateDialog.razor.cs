// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.VariableLibraries;

public partial class LibraryPortAllocateDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int LibraryId { get; set; }

    /// <summary>The keys the list starts with, when the dialog is opened for particular keys (the row
    /// menu of an empty <c>PORT_</c> entry) instead of the library's own defaults.</summary>
    [Parameter] public string[]? InitialKeys { get; set; }

    /// <summary>The empty <c>PORT_</c> keys the library already holds come first (R-85: they exist and
    /// only need a port); the simple pair is what a Compose file reads by default, and the blue-green
    /// four what the deployment stages expect, so they are offered rather than retyped, misspelt, and
    /// silently ignored by the guard.</summary>
    private const string EmptyPreset = "empty";
    private const string BlueGreenPreset = "blue-green";
    private const string SimplePreset = "simple";
    private const string FreePreset = "free";

    private static readonly string[] AllPresets = [EmptyPreset, SimplePreset, BlueGreenPreset, FreePreset];

    private static readonly string[] BlueGreenKeys =
        ["PORT_FRONT_BLUE", "PORT_BACK_BLUE", "PORT_FRONT_GREEN", "PORT_BACK_GREEN"];

    private static readonly string[] SimpleKeys = ["PORT_FRONT", "PORT_BACK"];

    private List<PortAllocationTargetDto> _servers = [];
    private List<VariableEntryDto> _created = [];
    private List<VariableEntryDto> _portEntries = [];
    private int? _serverId;
    private string _preset = SimplePreset;
    private string _keysInput = string.Join("\n", SimpleKeys);
    private string? _error;
    private string? _blocker;
    private bool _busy;

    private IEnumerable<string> Presets => _portEntries.Any(IsEmptyPortEntry)
        ? AllPresets
        : AllPresets.Where(preset => preset != EmptyPreset);

    private string PresetLabel(string preset) => preset switch
    {
        EmptyPreset => L["PortPresetEmpty"].Value,
        SimplePreset => L["PortPresetSimple"].Value,
        BlueGreenPreset => L["PortPresetBlueGreen"].Value,
        _ => L["PortPresetFree"].Value
    };

    private static bool IsEmptyPortEntry(VariableEntryDto entry) => string.IsNullOrWhiteSpace(entry.Value);

    /// <summary>What each requested key will do (R-86): keys the library lacks are created, empty ones
    /// are filled, and keys already holding a value are refused rather than overwritten.</summary>
    private (List<string> ToCreate, List<string> ToFill, List<string> Taken) KeyPlan()
    {
        var plan = (ToCreate: new List<string>(), ToFill: new List<string>(), Taken: new List<string>());
        foreach (var key in ParseKeys(_keysInput))
        {
            var existing = _portEntries.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
            if (existing is null) plan.ToCreate.Add(key);
            else if (IsEmptyPortEntry(existing)) plan.ToFill.Add(key);
            else plan.Taken.Add(key);
        }

        return plan;
    }

    private bool HasTakenKeys => KeyPlan().Taken.Count > 0;

    protected override async Task OnInitializedAsync()
    {
        if (await LibraryPortTargets.TryLoadAsync(Api, LibraryId) is not { } targets)
        {
            _error = L["LoadFailed"];
            return;
        }

        _servers = targets.Servers;
        _serverId = LibraryPortTargets.InitialServerId(targets);
        // The backend says WHY allocation is impossible (no project, no reachable server); showing
        // an enabled form that always fails would be worse than saying so up front.
        _blocker = targets.Blocker switch
        {
            PortAllocationBlocker.NoProject => L["PortAllocateNoProject"].Value,
            PortAllocationBlocker.NoServer => L["PortAllocateNoServer"].Value,
            _ => null
        };
        if (_blocker is not null) return;

        // A library that cannot be read is treated as holding nothing: the dialog then plans creations
        // and the backend, which is the authority, still refuses what cannot be written.
        _portEntries = await ReadPortEntriesAsync() ?? [];
        StartKeys();
    }

    /// <summary>The list starts from what the caller asked for, else from the empty <c>PORT_</c> keys
    /// the library already has, else from the simple pair.</summary>
    private void StartKeys()
    {
        if (InitialKeys is { Length: > 0 })
        {
            _preset = FreePreset;
            _keysInput = string.Join("\n", InitialKeys);
            return;
        }

        var empty = _portEntries.Where(IsEmptyPortEntry).Select(entry => entry.Key).ToList();
        if (empty.Count == 0) return;
        _preset = EmptyPreset;
        _keysInput = string.Join("\n", empty);
    }

    private void ApplyPreset()
    {
        if (_preset == EmptyPreset)
            _keysInput = string.Join("\n", _portEntries.Where(IsEmptyPortEntry).Select(entry => entry.Key));
        else if (_preset == BlueGreenPreset) _keysInput = string.Join("\n", BlueGreenKeys);
        else if (_preset == SimplePreset) _keysInput = string.Join("\n", SimpleKeys);
        // "free" leaves whatever the operator typed: switching to it must not erase their own keys.
    }

    private async Task AllocateAsync()
    {
        if (_serverId is not { } serverId) return;

        var keys = ParseKeys(_keysInput);
        if (keys.Count == 0)
        {
            _error = L["PortAllocateKeysRequired"];
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            // Read before writing: keys the library already held cannot be evidence that THIS call
            // wrote them. Allocating an existing key breaks the unique index, answers 500 and writes
            // nothing, yet the key is there afterwards.
            var before = await FindWrittenAsync(keys);
            var outcome = await Api.Variables.AllocateLibraryPortsAsync(
                LibraryId, new AllocateLibraryPortsRequest { ServerId = serverId, Keys = keys });
            if (outcome.Value is null)
            {
                // Override a reported refusal only when every requested key was absent before the
                // call and present afterward. If either verification read fails, retain the
                // refusal.
                var written = before is { Count: 0 } ? await FindWrittenAsync(keys) : null;
                if (written is not null && written.Count == keys.Count)
                {
                    Remember(written);
                    return;
                }

                _error = string.IsNullOrWhiteSpace(outcome.Error?.Message)
                    ? L["PortAllocateFailed"].Value
                    : outcome.Error.Message;
                return;
            }

            Remember(outcome.Value);
        }
        catch (HttpRequestException)
        {
            _error = L["PortAllocateFailed"].Value;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Keeps what was written, and the library's picture of itself in step: the keys now hold
    /// a value, so asking again would plan a refusal rather than a second allocation.</summary>
    private void Remember(List<VariableEntryDto> written)
    {
        _created = written;
        _portEntries = [.. _portEntries.Where(entry => written.All(item => item.Key != entry.Key)), .. written];
    }

    /// <summary>
    /// The entries among <paramref name="keys"/> the library actually holds now, or null when the
    /// library could not be read. Compared before and after the call to tell a real failure (nothing
    /// written) from a late failure after a successful write, which the operator otherwise sees as an
    /// error over ports that exist.
    /// </summary>
    private async Task<List<VariableEntryDto>?> FindWrittenAsync(IReadOnlyList<string> keys) =>
        await ReadPortEntriesAsync() is { } entries
            ? [.. entries.Where(entry => keys.Contains(entry.Key, StringComparer.Ordinal))]
            : null;

    /// <summary>The library's <c>PORT_</c> entries, or null when it could not be read: that re-read is
    /// the doubt, not the verdict, so a caller that needs certainty keeps the refusal it already has.</summary>
    private async Task<List<VariableEntryDto>?> ReadPortEntriesAsync()
    {
        try
        {
            var page = await Api.Variables.GetVariableEntriesPageAsync(
                LibraryId, page: 1, pageSize: 200, search: PortRegistryLimits.PortKeyPrefix);
            return [.. page.Items.Where(entry =>
                entry.Key.StartsWith(PortRegistryLimits.PortKeyPrefix, StringComparison.Ordinal))];
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>One key per line, blanks dropped. The PORT_ prefix is enforced by the backend, which
    /// refuses rather than renames, so a typo is reported instead of quietly producing a variable the
    /// preflight guard would never read.</summary>
    internal static List<string> ParseKeys(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        var keys = new List<string>();
        foreach (var line in input.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!keys.Contains(line, StringComparer.Ordinal)) keys.Add(line);
            if (keys.Count == PortRegistryLimits.MaxPortsPerAllocation) break;
        }
        return keys;
    }
}
