// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.VariableLibraries;

public partial class LibraryPortCheckDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int LibraryId { get; set; }

    private List<PortAllocationTargetDto> _servers = [];
    private int? _serverId;
    private PortCheckResultDto? _result;
    private string? _error;
    private bool _busy;

    private string ScanCaption => PortCheckDialog.FormatScanCaption(L, _result);

    private string ObservationText(PortCheckEntryDto entry) => PortCheckDialog.FormatObservation(L, entry);

    private List<VariableEntryDto> _conflicting = [];

    private static bool IsConflict(PortCheckEntryDto entry) =>
        entry.Verdict is PortCheckVerdict.TakenByAnotherOwner or PortCheckVerdict.ListeningUndeclared;

    /// <summary>The library's <c>PORT_</c> entries whose port is held by another owner or seen listening for
    /// somebody else: what the check offers to move to free ports.</summary>
    private async Task FindConflictsAsync(PortCheckResultDto result)
    {
        var badPorts = result.Entries
            .Where(IsConflict)
            .Select(entry => entry.Port)
            .ToHashSet();
        var entries = badPorts.Count == 0 ? [] : await LibraryPortTargets.TryReadPortEntriesAsync(Api, LibraryId) ?? [];
        _conflicting = [.. entries.Where(entry => LibraryPortTargets.TryReadPort(entry, out var port) && badPorts.Contains(port))];
    }

    /// <summary>Moves the conflicting keys to free ports: the new ports are previewed and listed
    /// (<c>KEY : old → new</c>), and nothing is written until the operator confirms.</summary>
    private async Task ReallocateAsync()
    {
        if (_serverId is not { } serverId || _conflicting.Count == 0) return;

        _busy = true;
        _error = null;
        try
        {
            var request = new AllocateLibraryPortsRequest { ServerId = serverId, Keys = [.. _conflicting.Select(entry => entry.Key)] };
            var preview = await Api.Variables.PreviewFreeLibraryPortsAsync(LibraryId, request);
            if (preview.Value is not { } fresh || fresh.Count != _conflicting.Count)
            {
                _error = string.IsNullOrWhiteSpace(preview.Error?.Message) ? L["PortAllocateFailed"].Value : preview.Error.Message;
                return;
            }

            var lines = _conflicting.Select((entry, index) => $"{entry.Key} : {entry.Value} → {fresh[index]}");
            var message = L["PortReallocateConfirm"].Value + "\n" + string.Join("\n", lines);
            var confirmed = await Dialog.Confirm(message, L["PortReallocate"].Value,
                new OmniConfirmOptions { OkButtonText = L["PortReallocate"].Value, CancelButtonText = L["GoBack"].Value });
            if (confirmed is not true) return;

            var outcome = await Api.Variables.ReallocateLibraryPortsAsync(LibraryId, request);
            if (outcome.Value is null)
            {
                _error = string.IsNullOrWhiteSpace(outcome.Error?.Message) ? L["PortAllocateFailed"].Value : outcome.Error.Message;
                return;
            }

            Dialog.Close(true);
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

    protected override async Task OnInitializedAsync()
    {
        if (await LibraryPortTargets.TryLoadAsync(Api, LibraryId) is not { } targets)
        {
            _error = L["LoadFailed"];
            return;
        }

        _servers = targets.Servers;
        _serverId = LibraryPortTargets.InitialServerId(targets);
    }

    private async Task CheckAsync()
    {
        if (_serverId is not { } serverId) return;

        _busy = true;
        _error = null;
        _result = null;
        try
        {
            var outcome = await Api.Variables.CheckLibraryPortsAsync(
                LibraryId, new AllocateLibraryPortsRequest { ServerId = serverId });
            if (outcome.Value is null)
            {
                // The refusal says what is missing (no PORT_* entry at all), which is more useful than
                // an empty verdict list.
                _error = string.IsNullOrWhiteSpace(outcome.Error?.Message)
                    ? L["LoadFailed"].Value
                    : outcome.Error.Message;
                return;
            }

            _result = outcome.Value;
            await FindConflictsAsync(outcome.Value);
        }
        catch (HttpRequestException)
        {
            _error = L["LoadFailed"].Value;
        }
        finally
        {
            _busy = false;
        }
    }
}
