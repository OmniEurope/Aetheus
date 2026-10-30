// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Settings;

public partial class CertificateAlertSettingsPanel
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private CertificateAlertSettingsDto? _settings;
    private EditModel _model = new();
    private string? _error;
    private bool _loading = true;
    private bool _saving;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            Apply(await Api.Settings.GetCertificateAlertsAsync());
        }
        catch (HttpRequestException)
        {
            _settings = null; // 401 on expired JWT - redirect handled by AuthProvider
        }
        finally
        {
            _loading = false;
        }
    }

    private void Apply(CertificateAlertSettingsDto? settings)
    {
        _settings = settings;
        if (settings is null) return;
        _model = new EditModel { LeadDays = [.. settings.LeadDays.OrderByDescending(days => days)] };
    }

    /// <summary>"7 days before the expiry": how an alert is read in the list.</summary>
    internal string LeadText(int days) => L[days == 1 ? "CertificateAlertsLeadOne" : "CertificateAlertsLeadMany", days];

    /// <summary>Adds the typed lead time when it is in range and not listed yet; says why otherwise.</summary>
    internal bool AddLeadTime()
    {
        if (_model.NewDays is not { } days)
        {
            _error = L["CertificateAlertsDaysRequired"];
            return false;
        }
        if (days < CertificateAlertLeadTimes.MinDays || days > CertificateAlertLeadTimes.MaxDays)
        {
            _error = L["CertificateAlertsDaysRange", CertificateAlertLeadTimes.MinDays, CertificateAlertLeadTimes.MaxDays];
            return false;
        }
        if (_model.LeadDays.Contains(days))
        {
            _error = L["CertificateAlertsDaysDuplicate", days];
            return false;
        }
        if (_model.LeadDays.Count >= CertificateAlertLeadTimes.MaxCount)
        {
            _error = L["CertificateAlertsTooMany", CertificateAlertLeadTimes.MaxCount];
            return false;
        }
        _model.LeadDays = [.. _model.LeadDays.Append(days).OrderByDescending(item => item)];
        _model.NewDays = null;
        _error = null;
        return true;
    }

    internal void RemoveLeadTime(int days)
    {
        _model.LeadDays = [.. _model.LeadDays.Where(item => item != days)];
        _error = null;
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        // A value typed and not added yet is added first, so pressing Enter does not lose it.
        if (_model.NewDays is not null && !AddLeadTime()) return;
        _saving = true;
        try
        {
            var outcome = await Api.Settings.UpdateCertificateAlertsAsync(
                new UpdateCertificateAlertSettingsRequest { LeadDays = [.. _model.LeadDays] });
            if (outcome.Value is { } saved)
            {
                Apply(saved);
                Toast.Success("Saved", "CertificateAlertsSaved");
            }
            else
            {
                Toast.Notify(OmniSeverity.Danger, "Error", outcome.ErrorMessage ?? L["SaveFailed"].Value);
            }
        }
        finally
        {
            _saving = false;
        }
    }

    internal sealed class EditModel
    {
        public List<int> LeadDays { get; set; } = [];

        public int? NewDays { get; set; }
    }
}
