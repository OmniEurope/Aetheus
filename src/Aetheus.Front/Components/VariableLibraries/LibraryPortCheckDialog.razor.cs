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
