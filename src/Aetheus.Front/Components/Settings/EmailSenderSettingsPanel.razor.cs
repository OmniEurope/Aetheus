// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Settings;

public partial class EmailSenderSettingsPanel
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private EmailSenderSettingsDto? _settings;
    private EditModel _model = null!; // set first thing in OnInitializedAsync, once the localizer is injected
    private List<OmniOption<SmtpTlsMode>> _tlsModes = [];
    private bool _loading = true;
    private bool _saving;
    private bool _preparing;

    private bool CanPrepare =>
        _settings is { CanPrepareNoReply: true, UseCustom: false }
        && _settings.Problem is EmailSenderProblem.NoReplyAccountMissing or EmailSenderProblem.NoReplyPasswordUnknown;

    private OmniSeverity EffectiveSeverity =>
        _settings?.EffectiveSource is EmailSenderSource.None or null ? OmniSeverity.Warning : OmniSeverity.Success;

    private string EffectiveTitle => _settings?.EffectiveSource switch
    {
        EmailSenderSource.ManagedNoReply => L["EmailSenderEffectiveDefault"],
        EmailSenderSource.Custom => L["EmailSenderEffectiveCustom"],
        _ => L["EmailSenderEffectiveNone"]
    };

    private string EffectiveText
    {
        get
        {
            if (_settings is null) return string.Empty;
            if (_settings.EffectiveSource != EmailSenderSource.None)
                return L["EmailSenderEffectiveDetail", _settings.EffectiveFromAddress ?? string.Empty,
                    _settings.EffectiveHost ?? string.Empty, _settings.EffectivePort ?? 0];
            return L[$"Enum_EmailSenderProblem_{_settings.Problem}", _settings.ProblemDetail ?? _settings.NoReplyAddress ?? string.Empty];
        }
    }

    protected override async Task OnInitializedAsync()
    {
        _model = new EditModel(L);
        _tlsModes = Enum.GetValues<SmtpTlsMode>()
            .Select(mode => new OmniOption<SmtpTlsMode>(mode, L.Localize(mode)))
            .ToList();
        try
        {
            Apply(await Api.Settings.GetEmailSenderAsync());
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

    private void Apply(EmailSenderSettingsDto? settings)
    {
        _settings = settings;
        if (settings is null) return;
        _model = new EditModel(L)
        {
            UseCustom = settings.UseCustom,
            Host = settings.Host,
            Port = settings.Port,
            TlsMode = settings.TlsMode,
            Username = settings.Username,
            FromAddress = settings.FromAddress
        };
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        try
        {
            var outcome = await Api.Settings.UpdateEmailSenderAsync(new UpdateEmailSenderSettingsRequest
            {
                UseCustom = _model.UseCustom,
                Host = _model.Host,
                Port = _model.Port,
                TlsMode = _model.TlsMode,
                Username = _model.Username,
                Password = string.IsNullOrEmpty(_model.Password) ? null : _model.Password,
                FromAddress = _model.FromAddress
            });
            if (outcome.Value is { } saved)
            {
                Apply(saved);
                Toast.Success("Saved", "EmailSenderSaved");
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

    /// <summary>The default sender works as far as Aetheus knows, yet the mail server may refuse its password (it
    /// was changed there, or the mailbox was recreated): the way to put both sides back in step.</summary>
    private bool CanReset =>
        _settings is { CanPrepareNoReply: true, UseCustom: false, EffectiveSource: EmailSenderSource.ManagedNoReply };

    private Task PrepareAsync()
    {
        if (_settings?.Problem == EmailSenderProblem.NoReplyPasswordUnknown)
            return GenerateAsync("EmailSenderPrepare", "EmailSenderPrepareResetConfirm");
        return GenerateAsync(null, null);
    }

    private Task ResetAsync() => GenerateAsync("EmailSenderReset", "EmailSenderResetConfirm");

    /// <summary>Generates the no-reply password, after the confirmation <paramref name="confirmKey"/> names (none when null).</summary>
    private async Task GenerateAsync(string? titleKey, string? confirmKey)
    {
        if (_preparing || _settings is null) return;
        if (titleKey is not null && confirmKey is not null)
        {
            var confirmed = await Dialog.Confirm(
                L[confirmKey, _settings.NoReplyAddress ?? string.Empty].Value,
                L[titleKey].Value,
                new OmniConfirmOptions { OkButtonText = L[titleKey].Value, CancelButtonText = L["GoBack"].Value });
            if (confirmed != true) return;
        }

        _preparing = true;
        try
        {
            var outcome = await Api.Settings.PrepareNoReplySenderAsync();
            if (outcome.Value is { } prepared)
            {
                Apply(prepared);
                Toast.Success("Saved", "EmailSenderPrepared");
            }
            else
            {
                Toast.Notify(OmniSeverity.Danger, "Error", outcome.ErrorMessage ?? L["SaveFailed"].Value);
            }
        }
        finally
        {
            _preparing = false;
        }
    }

    /// <summary>The messages are read through the localizer: OE shows a validation message as it is.</summary>
    private sealed class EditModel(IStringLocalizer localizer) : IValidatableObject
    {
        public bool UseCustom { get; set; }

        [StringLength(253)]
        public string Host { get; set; } = string.Empty;

        [Range(1, 65535)]
        public int Port { get; set; } = 587;

        public SmtpTlsMode TlsMode { get; set; } = SmtpTlsMode.StartTls;

        [StringLength(254)]
        public string Username { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Password { get; set; }

        [StringLength(254)]
        public string FromAddress { get; set; } = string.Empty;

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (!UseCustom) yield break;
            if (string.IsNullOrWhiteSpace(Host))
                yield return new ValidationResult(localizer["EmailSenderHostRequired"], [nameof(Host)]);
            if (!new EmailAddressAttribute().IsValid(FromAddress) || string.IsNullOrWhiteSpace(FromAddress))
                yield return new ValidationResult(localizer["EmailSenderFromInvalid"], [nameof(FromAddress)]);
        }
    }
}
