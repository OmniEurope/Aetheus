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
        new($"DKIM (TXT {DnsRecords.DkimSelector}._domainkey.{DnsRecords.Domain})", DnsRecords.DkimPublicKey, true)
    ];

    // R-511: OmniDialog listens to the keyboard, so every key press re-renders the dialog and hands it the
    // same parameters again. Copying the model there wiped what had just been typed: it is copied once.
    protected override void OnInitialized() => _model = Model.Clone();
    protected override void OnParametersSet() => _model.Mode = Mode;
    // R2-085: a second submit (Enter, then a click) must not close a second time: Close pops whatever
    // dialog is on top, the one underneath included.
    private bool _closed;
    private void Submit(MailDialogModel model)
    {
        if (_closed) return;
        _closed = true;
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

    public MailDialogMode Mode { get; set; }
    [StringLength(255)] public string Hostname { get; set; } = string.Empty;
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
    public MailDialogModel Clone() => (MailDialogModel)MemberwiseClone();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in ValidateDomain()) yield return result;
        foreach (var result in ValidateAccount()) yield return result;
        foreach (var result in ValidateAlias()) yield return result;
        if (Mode == MailDialogMode.RotateDkim && string.IsNullOrWhiteSpace(NewSelector))
            yield return new ValidationResult("Required", [nameof(NewSelector)]);
        foreach (var result in ValidateStackOperations()) yield return result;
    }

    private IEnumerable<ValidationResult> ValidateDomain()
    {
        if (Mode is MailDialogMode.AddDomain or MailDialogMode.Setup)
        {
            if (string.IsNullOrWhiteSpace(Domain))
                yield return new ValidationResult("Required", [nameof(Domain)]);
            else if (!DomainValidator.IsValid(Domain))
                yield return new ValidationResult("MailDomainInvalid", [nameof(Domain)]);
            // Recette R2-060: the helper refuses a selector it cannot put in a DNS name (mail-setup).
            if (!MailValidation.IsValidDkimSelector(DkimSelector))
                yield return new ValidationResult("MailDkimSelectorInvalid", [nameof(DkimSelector)]);
        }
        // Recette R2-060: mail-setup takes a full name only (a dot and an extension); a bare host name was
        // refused by the agent after the whole wizard.
        if (Mode == MailDialogMode.Setup && !DomainValidator.IsValid(Hostname))
            yield return new ValidationResult("MailHostnameInvalid", [nameof(Hostname)]);
    }

    private IEnumerable<ValidationResult> ValidateAccount()
    {
        if (Mode is MailDialogMode.AddAccount or MailDialogMode.Setup)
        {
            if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
                yield return new ValidationResult("Required", [nameof(Email), nameof(Password)]);
            else
            {
                if (!EmailValidator.IsValid(Email))
                    yield return new ValidationResult("MailEmailInvalid", [nameof(Email)]);
                if (!MailValidation.IsValidPassword(Password))
                    yield return new ValidationResult("MailPasswordInvalid", [nameof(Password)]);
            }
        }
    }

    private IEnumerable<ValidationResult> ValidateStackOperations() => Mode switch
    {
        MailDialogMode.RequestCertificate => EmailValidator.IsValid(Email)
            ? [] : [new ValidationResult("MailEmailInvalid", [nameof(Email)])],
        MailDialogMode.SpamThresholds => MailValidation.AreSpamThresholdsOrdered(GreylistScore, AddHeaderScore, RejectScore)
            ? [] : [new ValidationResult("MailSpamThresholdsInvalid", [nameof(RejectScore)])],
        MailDialogMode.LearnSpam => string.IsNullOrWhiteSpace(RawMessage)
            ? [new ValidationResult("Required", [nameof(RawMessage)])] : [],
        MailDialogMode.TestDelivery => ValidateTestDelivery(),
        MailDialogMode.Setup => ValidateSetupAddress(),
        _ => []
    };

    private IEnumerable<ValidationResult> ValidateTestDelivery()
    {
        if (string.IsNullOrWhiteSpace(Source) || !EmailValidator.IsValid(Source))
            yield return new ValidationResult("MailEmailInvalid", [nameof(Source)]);
        if (string.IsNullOrWhiteSpace(Destination) || !EmailValidator.IsValid(Destination))
            yield return new ValidationResult("MailEmailInvalid", [nameof(Destination)]);
    }

    // The setup helper creates the admin mailbox inside the configured domain.
    private IEnumerable<ValidationResult> ValidateSetupAddress()
    {
        if (!string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Domain)
            && !Email.EndsWith("@" + Domain, StringComparison.OrdinalIgnoreCase))
            yield return new ValidationResult("MailAdminEmailOutsideDomain", [nameof(Email)]);
    }

    private IEnumerable<ValidationResult> ValidateAlias()
    {
        if (Mode == MailDialogMode.AddAlias)
        {
            if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(Destination) || DomainId <= 0)
                yield return new ValidationResult("Required", [nameof(Source), nameof(Destination), nameof(DomainId)]);
            else
            {
                if (!EmailValidator.IsValid(Source))
                    yield return new ValidationResult("MailEmailInvalid", [nameof(Source)]);
                if (!EmailValidator.IsValid(Destination))
                    yield return new ValidationResult("MailEmailInvalid", [nameof(Destination)]);
            }
        }
    }
}
