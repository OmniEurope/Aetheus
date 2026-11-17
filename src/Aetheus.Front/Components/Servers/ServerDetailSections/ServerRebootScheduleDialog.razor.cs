// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R-8: the form of a server's daily reboot. The time zone is chosen in a closed list (UTC and the usual
/// IANA zones, plus the schedule's own zone when it is none of them), so the backend never has to refuse an unknown
/// zone. It returns a <see cref="SaveServerRebootScheduleRequest"/>; the card does the write.
/// </summary>
public partial class ServerRebootScheduleDialog
{
    /// <summary>The zones offered: UTC first, then the zones of Europe and the main ones elsewhere.</summary>
    internal static readonly IReadOnlyList<string> Zones =
    [
        ServerRebootOrder.UtcTimeZone, "Europe/Paris", "Europe/Brussels", "Europe/Amsterdam", "Europe/Berlin",
        "Europe/Madrid", "Europe/Rome", "Europe/Zurich", "Europe/London", "Europe/Dublin", "Europe/Lisbon",
        "Europe/Athens", "Europe/Helsinki", "Europe/Moscow", "America/New_York", "America/Chicago",
        "America/Denver", "America/Los_Angeles", "America/Toronto", "America/Sao_Paulo", "Asia/Dubai",
        "Asia/Kolkata", "Asia/Singapore", "Asia/Tokyo", "Australia/Sydney"
    ];

    /// <summary>The schedule to change; null to schedule a first reboot.</summary>
    [Parameter] public ServerRebootScheduleDto? Schedule { get; set; }

    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private readonly RebootForm _form = new();
    private IReadOnlyList<OmniOption<string>> _zoneOptions = [];

    protected override void OnInitialized()
    {
        var zones = Zones.ToList();
        if (Schedule is { } schedule)
        {
            _form.Time = new TimeOnly(schedule.Hour, schedule.Minute);
            _form.TimeZoneId = schedule.TimeZoneId;
            _form.Enabled = schedule.Enabled;
            if (!zones.Contains(schedule.TimeZoneId, StringComparer.Ordinal)) zones.Add(schedule.TimeZoneId);
        }
        _zoneOptions = [.. zones.Select(zone => new OmniOption<string>(zone, zone))];
    }

    private void Cancel() => Dialog.Close(null);

    private void Save() => Dialog.Close(new SaveServerRebootScheduleRequest
    {
        Hour = _form.Time!.Value.Hour,
        Minute = _form.Time.Value.Minute,
        TimeZoneId = _form.TimeZoneId,
        Enabled = _form.Enabled
    });

    private sealed class RebootForm
    {
        [Required]
        public TimeOnly? Time { get; set; } = new TimeOnly(4, 0);

        [Required]
        [StringLength(64, MinimumLength = 1)]
        public string TimeZoneId { get; set; } = ServerRebootOrder.UtcTimeZone;

        public bool Enabled { get; set; } = true;
    }
}
