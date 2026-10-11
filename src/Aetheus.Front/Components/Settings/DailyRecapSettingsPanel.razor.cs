// SPDX-License-Identifier: EUPL-1.2
using Microsoft.JSInterop;

namespace Aetheus.Front.Components.Settings;

public partial class DailyRecapSettingsPanel
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private DailyRecapSettingsDto? _settings;
    private EditModel _model = null!; // set first thing in OnInitializedAsync, once the localizer is injected
    private List<OmniOption<DailyRecapLanguage>> _languages = [];
    private DailyRecapSendResultDto? _sendResult;
    private DailyRecapPreviewDto? _preview;
    private ElementReference _previewHost;
    private bool _previewPending;
    private bool _loading = true;
    private bool _saving;
    private bool _sending;
    private bool _previewLoading;

    private OmniSeverity LastSeverity => _settings switch
    {
        { LastSentAt: null } => OmniSeverity.Info,
        { LastError: not null } => OmniSeverity.Warning,
        _ => OmniSeverity.Success
    };

    private string LastText
    {
        get
        {
            if (_settings is not { LastSentAt: { } sentAt }) return L["DailyRecapLastNever"];
            var text = L["DailyRecapLastDelivered", sentAt.ToString("g"),
                _settings.LastSentDate?.AddDays(-1).ToString("d") ?? string.Empty,
                _settings.LastDeliveredCount ?? 0, _settings.LastRecipientCount ?? 0].Value;
            return _settings.LastError is { } error ? $"{text} {L["DailyRecapLastError", error]}" : text;
        }
    }

    private string? _recipientError;

    private string RecipientsHint => _settings is { RecipientsAreDefault: true }
        ? L["DailyRecapRecipientsDefaultHint"]
        : L["DailyRecapRecipientsHint"];

    /// <summary>The saved list, which the next recap and "Send now" use, not the edits not yet saved.</summary>
    private string NextRecipientsText => _settings is { Recipients.Count: > 0 } settings
        ? L["DailyRecapNextRecipients", string.Join(", ", settings.Recipients)]
        : L["DailyRecapNextRecipientsNone"];

    private string SendNowHint => _settings is { Recipients.Count: > 0 } settings
        ? L["DailyRecapSendNowHint", string.Join(", ", settings.Recipients)]
        : L["DailyRecapNextRecipientsNone"];

    protected override async Task OnInitializedAsync()
    {
        _model = new EditModel(L);
        _languages = Enum.GetValues<DailyRecapLanguage>()
            .Select(language => new OmniOption<DailyRecapLanguage>(language, L.Localize(language)))
            .ToList();
        try
        {
            Apply(await Api.Settings.GetDailyRecapAsync());
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

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_previewPending || _preview is null) return;
        _previewPending = false;
        await Js.InvokeVoidAsync("Aetheus.renderEmailPreview", _previewHost, _preview.Html);
    }

    private void Apply(DailyRecapSettingsDto? settings)
    {
        _settings = settings;
        if (settings is null) return;
        _model = new EditModel(L)
        {
            Enabled = settings.Enabled,
            SendTime = settings.SendTime,
            TimeZoneId = settings.TimeZoneId,
            Language = settings.Language,
            Sections = Enum.GetValues<DailyRecapSection>()
                .Select(section => new SectionChoice(section) { Included = settings.Sections.Contains(section) })
                .ToList(),
            Recipients = [.. settings.Recipients]
        };
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        // A typed address not yet added is added first, so pressing Enter does not lose it.
        if (!string.IsNullOrWhiteSpace(_model.NewRecipient) && !AddRecipient()) return;
        if (_model.Enabled && _model.Recipients.Count == 0)
        {
            _recipientError = L["DailyRecapRecipientsRequired"];
            return;
        }
        _saving = true;
        try
        {
            var outcome = await Api.Settings.UpdateDailyRecapAsync(new UpdateDailyRecapSettingsRequest
            {
                Enabled = _model.Enabled,
                SendTime = _model.SendTime ?? new TimeOnly(7, 0),
                TimeZoneId = _model.TimeZoneId.Trim(),
                Language = _model.Language,
                Sections = [.. _model.Sections.Where(choice => choice.Included).Select(choice => choice.Section)],
                Recipients = [.. _model.Recipients]
            });
            if (outcome.Value is { } saved)
            {
                Apply(saved);
                Toast.Success("Saved", "DailyRecapSaved");
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

    private async Task SendNowAsync()
    {
        if (_sending) return;
        _sending = true;
        _sendResult = null;
        try
        {
            var outcome = await Api.Settings.SendDailyRecapNowAsync();
            _sendResult = outcome.Value ?? new DailyRecapSendResultDto
            {
                Status = DailyRecapSendStatus.Failed,
                Message = outcome.ErrorMessage ?? L["SaveFailed"].Value
            };
        }
        finally
        {
            _sending = false;
        }
    }

    private async Task TogglePreviewAsync()
    {
        if (_previewLoading) return;
        if (_preview is not null)
        {
            _preview = null;
            return;
        }

        _previewLoading = true;
        try
        {
            _preview = await Api.Settings.GetDailyRecapPreviewAsync();
            _previewPending = _preview is not null;
            if (_preview is null) Toast.Notify(OmniSeverity.Danger, "Error", L["LoadFailed"].Value);
        }
        catch (HttpRequestException)
        {
            Toast.Notify(OmniSeverity.Danger, "Error", L["LoadFailed"].Value);
        }
        finally
        {
            _previewLoading = false;
        }
    }

    private static OmniSeverity SendSeverity(DailyRecapSendResultDto result) => result.Status switch
    {
        DailyRecapSendStatus.Sent => OmniSeverity.Success,
        DailyRecapSendStatus.NotConfigured => OmniSeverity.Warning,
        _ => OmniSeverity.Danger
    };

    private string SendTitle(DailyRecapSendResultDto result) => result.Status == DailyRecapSendStatus.Sent
        ? L["DailyRecapSendSent", result.Delivered, string.Join(", ", result.Recipients)]
        : L["DailyRecapSendNotSent", result.Delivered, result.Recipients.Count];

    /// <summary>Adds the typed address when it is a valid e-mail not yet listed; says why otherwise.</summary>
    private bool AddRecipient()
    {
        var address = (_model.NewRecipient ?? string.Empty).Trim();
        if (address.Length == 0 || !new EmailAddressAttribute().IsValid(address) || address.Contains(' ', StringComparison.Ordinal))
        {
            _recipientError = L["DailyRecapRecipientInvalid", address];
            return false;
        }
        if (_model.Recipients.Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            _recipientError = L["DailyRecapRecipientDuplicate", address];
            return false;
        }
        _model.Recipients = [.. _model.Recipients, address];
        _model.NewRecipient = string.Empty;
        _recipientError = null;
        return true;
    }

    private void RemoveRecipient(string address)
    {
        _model.Recipients = [.. _model.Recipients.Where(item => !string.Equals(item, address, StringComparison.OrdinalIgnoreCase))];
        _recipientError = null;
    }

    private sealed class SectionChoice(DailyRecapSection section)
    {
        public DailyRecapSection Section { get; } = section;
        public bool Included { get; set; }
    }

    /// <summary>The messages are read through the localizer: OE shows a validation message as it is.</summary>
    private sealed class EditModel(IStringLocalizer localizer) : IValidatableObject
    {
        public bool Enabled { get; set; } = true;

        public TimeOnly? SendTime { get; set; } = new(7, 0);

        [StringLength(64)]
        public string TimeZoneId { get; set; } = "Europe/Paris";

        public DailyRecapLanguage Language { get; set; } = DailyRecapLanguage.French;

        public List<SectionChoice> Sections { get; set; } = [];

        public List<string> Recipients { get; set; } = [];

        [StringLength(254)]
        public string NewRecipient { get; set; } = string.Empty;

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (SendTime is null)
                yield return new ValidationResult(localizer["DailyRecapSendTimeRequired"], [nameof(SendTime)]);
            if (string.IsNullOrWhiteSpace(TimeZoneId))
                yield return new ValidationResult(localizer["DailyRecapTimeZoneRequired"], [nameof(TimeZoneId)]);
        }
    }
}
