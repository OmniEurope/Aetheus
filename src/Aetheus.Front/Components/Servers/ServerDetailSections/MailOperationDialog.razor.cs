// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class MailOperationDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Parameter] public MailDialogMode Mode { get; set; }
    [Parameter] public MailDialogModel Model { get; set; } = new();
    [Parameter] public int ServerId { get; set; }
    [Parameter] public IReadOnlyList<MailDomainDto> Domains { get; set; } = [];
    [Parameter] public MailDnsRecordsDto? DnsRecords { get; set; }
    /// <summary>Recette R3-47: the mail accounts of the server, offered as the sender of the delivery test.</summary>
    [Parameter] public IReadOnlyList<MailAccountDto> Accounts { get; set; } = [];

    private MailDialogModel _model = new();
    private string SubmitText => Mode switch
    {
        MailDialogMode.RotateDkim => L["Rotate"],
        MailDialogMode.RequestCertificate => L["MailObtainCertificate"],
        MailDialogMode.SpamThresholds => L["Save"],
        MailDialogMode.LearnSpam => L["MailLearn"],
        MailDialogMode.TestDelivery => L["Send"],
        _ => L["Add"]
    };
    private IReadOnlyList<DnsRecord> DnsRecordList => DnsRecords is null ? [] :
    [
        new(L["MxRecord"], DnsRecords.MxRecord, false), new("SPF (TXT)", DnsRecords.SpfRecord, false),
        new(L["DkimSelector"], DnsRecords.DkimSelector, false), new($"DMARC (TXT _dmarc.{DnsRecords.Domain})", DnsRecords.DmarcRecord, false),
        .. DnsRecords.DkimKeyStatus == MailDkimKeyStatus.Available ? [new DnsRecord(DkimRecordLabel, DnsRecords.DkimPublicKey, true)] : Array.Empty<DnsRecord>()
    ];

    private string DkimRecordLabel => DnsRecords is null ? string.Empty : $"DKIM (TXT {DnsRecords.DkimSelector}._domainkey.{DnsRecords.Domain})";

    /// <summary>The domains a new account name can be completed with, shown after the field as
    /// "@domain".</summary>
    internal IReadOnlyList<OmniOption<string>> AccountDomainOptions => Domains.ToOptions(item => item.Name, item => $"@{item.Name}");

    /// <summary>Why the DKIM record is missing from the list, or null when it is there.</summary>
    internal string? DkimUnavailableReason => DnsRecords?.DkimKeyStatus switch
    {
        MailDkimKeyStatus.Reading => L["MailDkimKeyReading"].Value,
        MailDkimKeyStatus.AgentOffline => L["MailDkimKeyAgentOffline"].Value,
        _ => null
    };

    // Copy the model only once because keyboard-driven dialog renders resend the same parameters
    // and must not reset typed input.
    protected override void OnInitialized()
    {
        _model = Model.Clone();
        _model.L = L;
        // A name alone is completed with the first domain until the user picks another.
        if (Mode == MailDialogMode.AddAccount && Domains.Count > 0
            && !Domains.Any(domain => string.Equals(domain.Name, _model.Domain, StringComparison.OrdinalIgnoreCase)))
            _model.Domain = Domains[0].Name;
        if (Mode == MailDialogMode.TestDelivery)
            _model.Source = PreselectedSender();
    }

    /// <summary>Recette R3-47: the existing accounts the delivery test can be sent from (the active ones).</summary>
    internal IReadOnlyList<OmniOption<string>> SenderOptions =>
        Accounts.Where(a => a.IsActive).ToOptions(a => a.Email, a => a.Email);

    /// <summary>
    /// Recette R3-47: the active accounts of the server whose address contains the text typed, offered as
    /// recipients; any other address stays accepted as typed (<c>AllowFreeText</c>).
    /// </summary>
    internal Task<IReadOnlyList<OmniOption<string>>> SearchRecipientsAsync(string text, CancellationToken ct)
    {
        var typed = text.Trim();
        IReadOnlyList<OmniOption<string>> matches = SenderOptions
            .Where(o => typed.Length == 0 || o.Value.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult(matches);
    }

    /// <summary>
    /// The administration account (admin or postmaster) when the server has one, else the sender already
    /// given when it is a known account, else the first account; the typed value is kept when there is no account.
    /// </summary>
    private string PreselectedSender()
    {
        var emails = SenderOptions.Select(o => o.Value).ToList();
        if (emails.Count == 0) return _model.Source;
        return emails.FirstOrDefault(e => IsAdministrationAddress(e))
            ?? emails.FirstOrDefault(e => string.Equals(e, _model.Source, StringComparison.OrdinalIgnoreCase))
            ?? emails[0];
    }

    private static bool IsAdministrationAddress(string email)
    {
        var local = email.Split('@')[0];
        return local.Equals("admin", StringComparison.OrdinalIgnoreCase)
            || local.Equals("postmaster", StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnParametersSet() => _model.Mode = Mode;
    // A second submit (Enter, then a click) must not close a second time: Close pops whatever
    // dialog is on top, the one underneath included.
    private bool _closed;
    private void Submit(MailDialogModel model)
    {
        if (_closed) return;
        _closed = true;
        if (Mode == MailDialogMode.AddAccount)
            model.Email = model.AccountAddress;
        Dialog.Close(model);
    }
    private sealed record DnsRecord(string Label, string Value, bool Multiline);
}

public enum MailDialogMode { AddDomain, AddAccount, DnsRecords, AddAlias, RotateDkim, Setup, RequestCertificate, SpamThresholds, LearnSpam, TestDelivery }

public sealed class MailDialogModel : IValidatableObject
{
    private const string DomainPattern = @"^[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)*\.[a-zA-Z]{2,}$";
    private static readonly RegularExpressionAttribute DomainValidator = new(DomainPattern);
    private static readonly EmailAddressAttribute EmailValidator = new();
    private IStringLocalizer? _localizer;

    public MailDialogMode Mode { get; set; }
    [StringLength(255)] public string Hostname { get; set; } = string.Empty;
    /// <summary>The domain added (AddDomain, Setup), or the domain a new account name is completed with (AddAccount).</summary>
    public string Domain { get; set; } = string.Empty;
    [StringLength(63)] public string DkimSelector { get; set; } = "default";
    public string Email { get; set; } = string.Empty;
    [StringLength(PasswordPolicy.MaximumLength)] public string Password { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public int DomainId { get; set; }
    public int QuotaMb { get; set; } = 1024;
    public string CurrentSelector { get; set; } = string.Empty;
    [StringLength(63)] public string NewSelector { get; set; } = string.Empty;
    public bool EnableSpamFilter { get; set; } = true;
    public double RejectScore { get; set; } = 15;
    public double AddHeaderScore { get; set; } = 6;
    public double GreylistScore { get; set; } = 4;
    public bool IsSpam { get; set; } = true;
    [StringLength(MailValidation.MaxLearnMessageLength)] public string RawMessage { get; set; } = string.Empty;

    /// <summary>Validation messages require the form localizer because <see
    /// cref="IValidatableObject"/> messages are displayed directly. Missing localization fails
    /// explicitly instead of exposing resource keys.</summary>
    internal IStringLocalizer L
    {
        get => _localizer ?? throw new InvalidOperationException($"{nameof(MailDialogModel)}.{nameof(L)} must be set before the model is validated.");
        set => _localizer = value;
    }

    /// <summary> The address of a new account. A name alone (<c>contact</c>) is completed with <see
    /// cref="Domain"/>; an address typed in full is kept as it is. </summary>
    internal string AccountAddress
    {
        get
        {
            var typed = Email.Trim();
            return typed.Contains('@', StringComparison.Ordinal) || Domain.Length == 0 ? typed : $"{typed}@{Domain}";
        }
    }

    /// <summary>The Let's Encrypt contact is optional; blank means none (the back then registers
    /// without one).</summary>
    internal string? AcmeEmail => string.IsNullOrWhiteSpace(Email) ? null : Email.Trim();

    public MailDialogModel Clone() => (MailDialogModel)MemberwiseClone();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in ValidateDomain()) yield return result;
        foreach (var result in ValidateAccount()) yield return result;
        foreach (var result in ValidateAlias()) yield return result;
        if (Mode == MailDialogMode.RotateDkim && string.IsNullOrWhiteSpace(NewSelector))
            yield return new ValidationResult(L["Required"], [nameof(NewSelector)]);
        foreach (var result in ValidateStackOperations()) yield return result;
    }

    private IEnumerable<ValidationResult> ValidateDomain()
    {
        if (Mode is MailDialogMode.AddDomain or MailDialogMode.Setup)
        {
            if (string.IsNullOrWhiteSpace(Domain))
                yield return new ValidationResult(L["Required"], [nameof(Domain)]);
            else if (!DomainValidator.IsValid(Domain))
                yield return new ValidationResult(L["MailDomainInvalid"], [nameof(Domain)]);
            // The helper refuses a selector it cannot put in a DNS name (mail-setup).
            if (!MailValidation.IsValidDkimSelector(DkimSelector))
                yield return new ValidationResult(L["MailDkimSelectorInvalid"], [nameof(DkimSelector)]);
        }
        // Mail setup requires a fully qualified hostname so the wizard rejects names the agent
        // cannot accept.
        if (Mode == MailDialogMode.Setup && !DomainValidator.IsValid(Hostname))
            yield return new ValidationResult(L["MailHostnameInvalid"], [nameof(Hostname)]);
    }

    private IEnumerable<ValidationResult> ValidateAccount()
    {
        if (Mode is not (MailDialogMode.AddAccount or MailDialogMode.Setup)) yield break;
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            yield return new ValidationResult(L["Required"], [nameof(Email), nameof(Password)]);
            yield break;
        }
        if (Mode == MailDialogMode.Setup && !EmailValidator.IsValid(Email))
            yield return new ValidationResult(L["MailEmailInvalid"], [nameof(Email)]);
        // The back creates the account only for an address its own rule accepts.
        if (Mode == MailDialogMode.AddAccount && !MailValidation.IsValidEmail(AccountAddress))
            yield return new ValidationResult(L["MailAccountAddressInvalid"], [nameof(Email)]);
        if (!MailValidation.IsValidPassword(Password))
            yield return new ValidationResult(
                L["MailPasswordInvalid", PasswordPolicy.MinimumLength, PasswordPolicy.MaximumLength], [nameof(Password)]);
    }

    private IEnumerable<ValidationResult> ValidateStackOperations() => Mode switch
    {
        // Blank is accepted (no Let's Encrypt contact); anything typed must be an address.
        MailDialogMode.RequestCertificate => AcmeEmail is null || MailValidation.IsValidEmail(AcmeEmail)
            ? [] : [new ValidationResult(L["MailEmailInvalid"], [nameof(Email)])],
        MailDialogMode.SpamThresholds => MailValidation.AreSpamThresholdsOrdered(GreylistScore, AddHeaderScore, RejectScore)
            ? [] : [new ValidationResult(L["MailSpamThresholdsInvalid"], [nameof(RejectScore)])],
        MailDialogMode.LearnSpam => string.IsNullOrWhiteSpace(RawMessage)
            ? [new ValidationResult(L["Required"], [nameof(RawMessage)])] : [],
        MailDialogMode.TestDelivery => ValidateTestDelivery(),
        MailDialogMode.Setup => ValidateSetupAddress(),
        _ => []
    };

    private IEnumerable<ValidationResult> ValidateTestDelivery()
    {
        if (string.IsNullOrWhiteSpace(Source) || !EmailValidator.IsValid(Source))
            yield return new ValidationResult(L["MailEmailInvalid"], [nameof(Source)]);
        if (string.IsNullOrWhiteSpace(Destination) || !EmailValidator.IsValid(Destination))
            yield return new ValidationResult(L["MailEmailInvalid"], [nameof(Destination)]);
    }

    // The setup helper creates the admin mailbox inside the configured domain.
    private IEnumerable<ValidationResult> ValidateSetupAddress()
    {
        if (!string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Domain)
            && !Email.EndsWith("@" + Domain, StringComparison.OrdinalIgnoreCase))
            yield return new ValidationResult(L["MailAdminEmailOutsideDomain"], [nameof(Email)]);
    }

    private IEnumerable<ValidationResult> ValidateAlias()
    {
        if (Mode == MailDialogMode.AddAlias)
        {
            if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(Destination) || DomainId <= 0)
                yield return new ValidationResult(L["Required"], [nameof(Source), nameof(Destination), nameof(DomainId)]);
            else
            {
                if (!EmailValidator.IsValid(Source))
                    yield return new ValidationResult(L["MailEmailInvalid"], [nameof(Source)]);
                if (!EmailValidator.IsValid(Destination))
                    yield return new ValidationResult(L["MailEmailInvalid"], [nameof(Destination)]);
            }
        }
    }
}
