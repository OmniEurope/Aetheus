// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Shared;

public partial class PortCheckDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>Pre-selected server. When null the dialog asks which server to check.</summary>
    [Parameter] public int? ServerId { get; set; }

    private List<ServerDto> _servers = [];
    private int? _selectedServerId;
    private string _portsInput = string.Empty;
    private string? _error;
    private bool _busy;
    private bool _scanning;
    private PortCheckResultDto? _result;

    private int? EffectiveServerId => ServerId ?? _selectedServerId;

    private string ScanCaption => FormatScanCaption(L, _result);

    /// <summary>Which scan the verdicts come from; shared with the library port check dialog.</summary>
    internal static string FormatScanCaption(IStringLocalizer<AppStrings> l, PortCheckResultDto? result) =>
        result?.LastScanAt is { } at
            ? string.Format(CultureInfo.CurrentCulture, l["PortLastScan"].Value, at.ToString("g", CultureInfo.CurrentCulture))
            : l["PortNeverScanned"].Value;

    /// <summary>What the last scan saw on the port; shared with the library port check dialog.</summary>
    internal static string FormatObservation(IStringLocalizer<AppStrings> l, PortCheckEntryDto entry) => entry.Observation switch
    {
        PortObservationState.Listening => string.IsNullOrWhiteSpace(entry.ObservedHolder)
            ? l["PortListeningYes"].Value
            : $"{l["PortListeningYes"].Value} - {entry.ObservedHolder}",
        PortObservationState.NotListening => l["PortListeningNo"].Value,
        _ => l["PortListeningUnknown"].Value
    };

    /// <summary>Reserve this detail line for claim provenance and scan evidence that the verdict
    /// badge cannot express; omit it when an unclaimed, unanswered port needs no
    /// explanation.</summary>
    private string DetailText(PortCheckEntryDto entry)
    {
        var claim = entry.IsFree || entry.Source is null ? null : L.Localize(entry.Source.Value);
        var seen = entry.Observation == PortObservationState.NotListening ? null : FormatObservation(L, entry);
        return string.Join(" · ", new[] { claim, seen }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// Queues a real scan, then re-runs the check. It does not wait for the agent: the task is
    /// asynchronous, so the re-check may still show the previous scan and the caption says which one
    /// it is, rather than the dialog pretending the refresh already landed.
    /// </summary>
    private async Task RefreshScanAsync()
    {
        if (EffectiveServerId is not { } serverId) return;

        _scanning = true;
        _error = null;
        try
        {
            var outcome = await Api.ServerTools.ObservePortsAsync(serverId);
            if (outcome.Value is null)
            {
                _error = string.IsNullOrWhiteSpace(outcome.Error?.Message)
                    ? L["PortScanFailed"].Value
                    : outcome.Error.Message;
                return;
            }
        }
        catch (HttpRequestException)
        {
            _error = L["PortScanFailed"].Value;
            return;
        }
        finally
        {
            _scanning = false;
        }

        await CheckAsync();
    }

    protected override async Task OnInitializedAsync()
    {
        if (ServerId is not null) return;
        try
        {
            _servers = await Api.Servers.GetAllServersAsync();
        }
        catch (HttpRequestException)
        {
            _error = L["LoadFailed"];
        }
    }

    private async Task CheckAsync()
    {
        _error = null;
        _result = null;
        if (EffectiveServerId is not { } serverId) return;

        var ports = ParsePorts(_portsInput);
        if (ports.Count == 0)
        {
            _error = L["PortsInvalid"];
            return;
        }

        _busy = true;
        try
        {
            _result = await Api.ServerTools.CheckPortsAsync(serverId, new PortCheckRequest { Ports = ports });
            if (_result is null) _error = L["LoadFailed"];
        }
        catch (HttpRequestException)
        {
            _error = L["LoadFailed"];
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Accepts what an operator actually types: "10041, 10042", "10041 10042", "10041;10042".
    /// A token that is not a port is dropped rather than failing the whole input, and an input that
    /// yields nothing is reported instead of silently checking an empty set.
    /// </summary>
    internal static List<int> ParsePorts(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        var ports = new List<int>();
        foreach (var token in input.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535
                && !ports.Contains(port))
            {
                ports.Add(port);
            }
            if (ports.Count == PortRegistryLimits.MaxPortsPerCheck) break;
        }
        return ports;
    }
}
