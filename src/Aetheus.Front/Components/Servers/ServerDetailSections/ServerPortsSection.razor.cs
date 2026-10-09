// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerPortsSection
{
    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>From the server DTO: the agent publishes <c>ports.observe</c>. Null = the agent has
    /// never phoned home, so the scan is neither offered nor declared impossible.</summary>
    [Parameter] public bool? PortObservationAvailable { get; set; }

    /// <summary>When the host's ports were last scanned, null when never.</summary>
    [Parameter] public DateTime? PortsObservedAt { get; set; }

    /// <summary>The agent is reachable. A scan queued for an offline agent would sit Pending.</summary>
    [Parameter] public bool AgentOnline { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>Whether a claimed port was seen listening by the most recent scan.</summary>
    internal enum ListeningState
    {
        /// <summary>The server has never been scanned; saying "not listening" would be a guess.</summary>
        Unknown,
        Listening,
        NotListening
    }

    /// <summary>One line of the grid: a claim, or a listener nobody claimed.</summary>
    internal sealed record PortRow
    {
        public int Id { get; init; }
        public int Port { get; init; }

        /// <summary>Shown as its own column: 53/udp and 53/tcp are two rows about two different
        /// ports, and without it the grid would read as a duplicate.</summary>
        public string Protocol { get; init; } = PortRegistryLimits.TcpProtocol;

        public string OwnerLabel { get; init; } = string.Empty;
        public int? ProjectId { get; init; }
        public string? ProjectName { get; init; }
        public PortReservationSource Source { get; init; }
        public DateTime DeclaredAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public ListeningState Listening { get; init; }
    }

    private List<PortReservationDto> _reservations = [];
    private List<PortRow> _rows = [];
    private OmniDataGrid<PortRow>? _grid;

    // Recette R-210: the Source and Listening filters show the same words as the cells, built once for
    // stable delegates.
    private Func<string, string>? _sourceText;
    private Func<string, string> SourceText => _sourceText ??= GridFilterText.ForEnum<PortReservationSource>(L);
    private Func<string, string>? _listeningFilterText;
    private Func<string, string> ListeningFilterText => _listeningFilterText ??= value =>
        Enum.TryParse<ListeningState>(value, ignoreCase: true, out var state) ? ListeningText(state) : value;
    private List<ProjectDto> _projects = [];
    private readonly NewReservationModel _form = new();
    private string? _addError;
    private string? _scanError;
    private bool _loading = true;
    private bool _busy;
    private bool _scanning;
    private int? _loadedServerId;
    private int _loadGeneration;

    private bool CanWrite => Permissions.CanWrite(ResourceType.Server, ServerId);

    private bool CanScan => !_scanning && AgentOnline && PortObservationAvailable == true;

    /// <summary>
    /// The newest scan this page knows of. <see cref="PortsObservedAt"/> comes from the server DTO the
    /// layout loaded once, and a scan reported by a heartbeat never refreshes it, so on its own it can
    /// still say "never scanned" above rows that scan just stamped. Every scan stamps its rows with its
    /// own time (<c>PortObservationWriter</c>), so the newest row stamp is a server-clock scan time too.
    /// </summary>
    internal DateTime? LastScanAt => Latest(PortsObservedAt, _newestRowStamp);

    private DateTime? _newestRowStamp;

    private static DateTime? Latest(DateTime? left, DateTime? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;

    private string ScanCaption => LastScanAt is { } at
        ? string.Format(CultureInfo.CurrentCulture, L["PortLastScan"].Value, at.ToString("g", CultureInfo.CurrentCulture))
        : L["PortNeverScanned"].Value;

    /// <summary>Says WHY the button is unavailable instead of just greying it out.</summary>
    private string ScanTooltip => PortObservationAvailable switch
    {
        false => L["PortScanUnsupported"].Value,
        null => L["PortScanAgentUnknown"].Value,
        _ => AgentOnline ? L["PortScanRefresh"].Value : L["PortScanAgentOffline"].Value
    };

    private sealed class NewReservationModel
    {
        [Range(1, 65535)]
        public int Port { get; set; } = 10000;

        [Required]
        [StringLength(PortRegistryLimits.MaxOwnerLabelLength, MinimumLength = 1)]
        public string OwnerLabel { get; set; } = string.Empty;

        public int? ProjectId { get; set; }

        /// <summary>How many free ports the "allocate" action should hand out.</summary>
        [Range(1, PortRegistryLimits.MaxPortsPerAllocation)]
        public int AllocateCount { get; set; } = 1;
    }

    private int _rangeFrom = PortRegistryLimits.DefaultRangeFrom;
    private int _rangeTo = PortRegistryLimits.DefaultRangeTo;
    private bool _rangeIsExplicit;

    private string RangeCaption => _rangeIsExplicit
        ? L["PortRangeExplicit"].Value
        : L["PortRangeDefault"].Value;

    /// <summary>
    /// Hands out free ports and reserves them in one step. Reuses the holder and project of the add
    /// form: the two actions answer the same two questions, and the only difference is who picks the
    /// port number.
    /// </summary>
    private async Task AllocateAsync()
    {
        if (string.IsNullOrWhiteSpace(_form.OwnerLabel))
        {
            _addError = L["PortHolderRequired"].Value;
            return;
        }

        _busy = true;
        _addError = null;
        try
        {
            var outcome = await Api.ServerTools.AllocatePortsAsync(ServerId, new AllocatePortsRequest
            {
                Count = _form.AllocateCount,
                OwnerLabel = _form.OwnerLabel.Trim(),
                ProjectId = _form.ProjectId
            });

            if (outcome.Value is null)
            {
                // The refusal names how many ports the window still has, which is what tells the
                // operator to widen it rather than to retry.
                _addError = string.IsNullOrWhiteSpace(outcome.Error?.Message)
                    ? L["PortAllocateFailed"].Value
                    : outcome.Error.Message;
                return;
            }

            Toast.Success("PortAllocated");
            await LoadAsync();
        }
        catch (HttpRequestException)
        {
            _addError = L["PortAllocateFailed"].Value;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveRangeAsync()
    {
        _busy = true;
        _addError = null;
        try
        {
            var saved = await Api.ServerTools.SetPortRangeAsync(
                ServerId, new PortRangeDto { From = _rangeFrom, To = _rangeTo });
            if (saved is null)
            {
                _addError = L["PortRangeFailed"].Value;
                return;
            }

            _rangeFrom = saved.From;
            _rangeTo = saved.To;
            _rangeIsExplicit = saved.IsExplicit;
            Toast.Success("PortRangeSaved");
        }
        catch (HttpRequestException)
        {
            _addError = L["PortRangeFailed"].Value;
        }
        finally
        {
            _busy = false;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId == ServerId)
        {
            // The scan timestamp arrives through the server DTO, so a heartbeat can move it without
            // the reservations changing; the rows are rebuilt so "listening" follows it.
            BuildRows();
            return;
        }
        _loadedServerId = ServerId;
        await LoadAsync();
    }

    /// <summary>
    /// Reads the claims and the range. <paramref name="live"/> is the task-completion path (a finished
    /// scan): the grid stays on screen (no loader) and is told before the new rows arrive, so the ports
    /// it did not hold read bold (recette R-227).
    /// </summary>
    private async Task LoadAsync(bool live = false)
    {
        var serverId = ServerId;
        var generation = Interlocked.Increment(ref _loadGeneration);
        if (!live) _loading = true;
        try
        {
            var reservations = await Api.ServerTools.GetPortReservationsAsync(serverId);
            // The project list only feeds the "linked project" dropdown, so a server with no projects
            // (or a caller who cannot read them) still gets a usable page.
            var projects = CanWrite ? await Api.Servers.GetServerProjectsAsync(serverId) : [];
            var range = await Api.ServerTools.GetPortRangeAsync(serverId);
            if (serverId != ServerId || generation != _loadGeneration) return;
            if (live && _grid is not null) await _grid.RefreshAsync();
            _reservations = reservations;
            _projects = projects;
            if (range is not null)
            {
                _rangeFrom = range.From;
                _rangeTo = range.To;
                _rangeIsExplicit = range.IsExplicit;
            }
            BuildRows();
        }
        catch (HttpRequestException)
        {
            if (serverId != ServerId || generation != _loadGeneration) return;
            _reservations = [];
            _projects = [];
            _rows = [];
            Toast.Error("PortRegistryLoadFailed");
        }
        finally
        {
            if (serverId == ServerId && generation == _loadGeneration) _loading = false;
        }
    }

    /// <summary>
    /// Crosses the two sources into one list. A claim keeps its row and gains a listening verdict; an
    /// observed port that a claim already covers is not repeated, because it is the same port, and the
    /// claim is the row an operator acts on. An observed port nobody claimed stays visible on its own -
    /// that is the discrepancy worth seeing.
    /// </summary>
    private void BuildRows()
    {
        var claimed = _reservations
            .Where(reservation => reservation.Source != PortReservationSource.Observed)
            .ToList();
        // FRONT-018: a port is the pair (number, protocol). A TCP claim on 53 says nothing about a UDP
        // listener on 53, which stays visible as an unclaimed observation.
        var claimedPorts = claimed.Select(PortKey).ToHashSet();
        _newestRowStamp = _reservations.Max(reservation => reservation.ObservedAt);

        var rows = new List<PortRow>(_reservations.Count);
        foreach (var reservation in claimed)
            rows.Add(ToRow(reservation, ListeningFor(reservation)));

        foreach (var observed in _reservations.Where(reservation =>
                     reservation.Source == PortReservationSource.Observed
                     && !claimedPorts.Contains(PortKey(reservation))))
        {
            rows.Add(ToRow(observed, ListeningState.Listening));
        }

        _rows = [.. rows.OrderBy(row => row.Port)];
    }

    private static (int Port, string Protocol) PortKey(PortReservationDto reservation) =>
        (reservation.Port, reservation.Protocol.ToLowerInvariant());

    /// <summary>
    /// A claim is listening when its observation stamp is at least as recent as the last scan we know
    /// of. Comparing against the server's scan time, rather than merely checking that a stamp exists, is
    /// what keeps an old sighting from being read as a live listener.
    ///
    /// The comparison is "not older than" rather than "equal" because the two values arrive on
    /// different responses: the reservations come from this section, the scan time from the server the
    /// layout keeps refreshed on heartbeats, so a scan landing between the two would otherwise turn
    /// every live listener into "not observed" until the next reload.
    /// </summary>
    private ListeningState ListeningFor(PortReservationDto reservation)
    {
        if (LastScanAt is not { } lastScan) return ListeningState.Unknown;
        return reservation.ObservedAt is { } stamp && stamp >= lastScan
            ? ListeningState.Listening
            : ListeningState.NotListening;
    }

    private static PortRow ToRow(PortReservationDto reservation, ListeningState listening) => new()
    {
        Id = reservation.Id,
        Port = reservation.Port,
        Protocol = reservation.Protocol,
        OwnerLabel = reservation.OwnerLabel,
        ProjectId = reservation.ProjectId,
        ProjectName = reservation.ProjectName,
        Source = reservation.Source,
        DeclaredAt = reservation.DeclaredAt,
        UpdatedAt = reservation.UpdatedAt,
        Listening = listening
    };

    private async Task AddAsync(NewReservationModel _)
    {
        _busy = true;
        _addError = null;
        try
        {
            var outcome = await Api.ServerTools.CreatePortReservationAsync(ServerId, new CreatePortReservationRequest
            {
                Port = _form.Port,
                OwnerLabel = _form.OwnerLabel.Trim(),
                ProjectId = _form.ProjectId
            });

            if (outcome.Value is null)
            {
                // The rejection names the current holder; showing "failed" instead would hide the one
                // fact the operator came for.
                _addError = string.IsNullOrWhiteSpace(outcome.Error?.Message)
                    ? L["PortReservationFailed"].Value
                    : outcome.Error.Message;
                return;
            }

            _form.OwnerLabel = string.Empty;
            _form.ProjectId = null;
            Toast.Success("PortReservationAdded");
            // Reloaded rather than appended: the creation response carries no project name.
            await LoadAsync();
        }
        catch (HttpRequestException)
        {
            _addError = L["PortReservationFailed"].Value;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Turns an observed listener into a manual reservation the registry will defend. The observation
    /// row is left alone: the next scan owns it, and deleting it here would only make it come back.
    /// </summary>
    private async Task ConvertAsync(PortRow row)
    {
        _busy = true;
        _addError = null;
        try
        {
            var outcome = await Api.ServerTools.CreatePortReservationAsync(ServerId, new CreatePortReservationRequest
            {
                Port = row.Port,
                OwnerLabel = row.OwnerLabel
            });

            if (outcome.Value is null)
            {
                _addError = string.IsNullOrWhiteSpace(outcome.Error?.Message)
                    ? L["PortReservationFailed"].Value
                    : outcome.Error.Message;
                return;
            }

            Toast.Success("PortReservationAdded");
            await LoadAsync();
        }
        catch (HttpRequestException)
        {
            _addError = L["PortReservationFailed"].Value;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ObserveAsync()
    {
        _scanning = true;
        _scanError = null;
        try
        {
            var outcome = await Api.ServerTools.ObservePortsAsync(ServerId);
            if (outcome.Value is null)
            {
                _scanError = string.IsNullOrWhiteSpace(outcome.Error?.Message)
                    ? L["PortScanFailed"].Value
                    : outcome.Error.Message;
                return;
            }

            // The scan is a queued agent task: the page cannot show its result yet, and saying
            // otherwise would be a green for work not done.
            Toast.Success("PortScanQueued");
        }
        catch (HttpRequestException)
        {
            _scanError = L["PortScanFailed"].Value;
        }
        finally
        {
            _scanning = false;
        }
    }

    private async Task ReleaseAsync(PortRow row)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(CultureInfo.CurrentCulture, L["PortReleaseConfirm"].Value, row.Port, row.OwnerLabel),
            L["PortRelease"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["PortRelease"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        _busy = true;
        try
        {
            if ((await Api.ServerTools.ReleasePortReservationAsync(ServerId, row.Id)).Success)
            {
                Toast.Success("PortReservationReleased");
                await LoadAsync();
            }
            else
            {
                Toast.Error("PortReservationFailed");
            }
        }
        catch (HttpRequestException)
        {
            Toast.Error("PortReservationFailed");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Reloads after an agent task completes, so a finished scan shows without a page reload.</summary>
    public async Task HandleTaskCompletedAsync(TaskCompletedNotification _)
    {
        await LoadAsync(live: true);
        StateHasChanged();
    }

    private static OmniTone SourceBadge(PortReservationSource source) => source switch
    {
        PortReservationSource.Declared => OmniTone.Accent,
        PortReservationSource.Manual => OmniTone.Neutral,
        // Observed: seen listening without anybody declaring it, which is a discrepancy to look at.
        _ => OmniTone.Warning
    };

    private static OmniTone ListeningBadge(ListeningState state) => state switch
    {
        ListeningState.Listening => OmniTone.Success,
        ListeningState.NotListening => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    private string ListeningText(ListeningState state) => state switch
    {
        ListeningState.Listening => L["PortListeningYes"],
        ListeningState.NotListening => L["PortListeningNo"],
        _ => L["PortListeningUnknown"]
    };
}
