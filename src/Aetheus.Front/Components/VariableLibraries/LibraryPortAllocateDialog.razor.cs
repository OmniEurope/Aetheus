// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.VariableLibraries;

public partial class LibraryPortAllocateDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int LibraryId { get; set; }

    /// <summary>The blue-green pair is what the deployment stages already expect, so it is offered
    /// as a preset rather than left to be retyped, misspelt, and silently ignored by the guard.</summary>
    private const string BlueGreenPreset = "blue-green";
    private const string SimplePreset = "simple";
    private const string FreePreset = "free";

    private static readonly string[] Presets = [BlueGreenPreset, SimplePreset, FreePreset];

    private static readonly string[] BlueGreenKeys =
        ["PORT_FRONT_BLUE", "PORT_BACK_BLUE", "PORT_FRONT_GREEN", "PORT_BACK_GREEN"];

    private static readonly string[] SimpleKeys = ["PORT_FRONT", "PORT_BACK"];

    private List<PortAllocationTargetDto> _servers = [];
    private List<VariableEntryDto> _created = [];
    private int? _serverId;
    private string _preset = BlueGreenPreset;
    private string _keysInput = string.Join("\n", BlueGreenKeys);
    private string? _error;
    private string? _blocker;
    private bool _busy;

    protected override async Task OnInitializedAsync()
    {
        if (await LibraryPortTargets.TryLoadAsync(Api, LibraryId) is not { } targets)
        {
            _error = L["LoadFailed"];
            return;
        }

        _servers = targets.Servers;
        _serverId = targets.PreselectedServerId;
        // The backend says WHY allocation is impossible (no project, no reachable server); showing
        // an enabled form that always fails would be worse than saying so up front.
        _blocker = targets.Blocker switch
        {
            PortAllocationBlocker.NoProject => L["PortAllocateNoProject"].Value,
            PortAllocationBlocker.NoServer => L["PortAllocateNoServer"].Value,
            _ => null
        };
    }

    private void ApplyPreset()
    {
        if (_preset == BlueGreenPreset) _keysInput = string.Join("\n", BlueGreenKeys);
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
                    _created = written;
                    return;
                }

                _error = string.IsNullOrWhiteSpace(outcome.Error?.Message)
                    ? L["PortAllocateFailed"].Value
                    : outcome.Error.Message;
                return;
            }

            _created = outcome.Value;
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

    /// <summary>
    /// The entries among <paramref name="keys"/> the library actually holds now, or null when the
    /// library could not be read. Compared before and after the call to tell a real failure (nothing
    /// written) from a late failure after a successful write, which the operator otherwise sees as an
    /// error over ports that exist.
    /// </summary>
    private async Task<List<VariableEntryDto>?> FindWrittenAsync(IReadOnlyList<string> keys)
    {
        try
        {
            var page = await Api.Variables.GetVariableEntriesPageAsync(
                LibraryId, page: 1, pageSize: PortRegistryLimits.MaxPortsPerAllocation * 4);
            return [.. page.Items.Where(entry => keys.Contains(entry.Key, StringComparer.Ordinal))];
        }
        catch (HttpRequestException)
        {
            // The re-read is the doubt, not the verdict: if it cannot be done, the refusal stands.
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
