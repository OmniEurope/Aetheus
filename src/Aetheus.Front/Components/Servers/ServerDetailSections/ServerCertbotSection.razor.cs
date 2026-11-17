// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerCertbotSection : ServerActionSectionBase
{
    [Inject] private ApiClient Api { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    private CertbotDataDto Certbot => Server.Certbot;

    // The renewal filter lists the translated conventions.
    private Func<string, string>? _conventionFilterText;
    private Func<string, string> ConventionFilterText => _conventionFilterText ??= GridFilterText.ForEnum<CertbotRenewalConvention>(L);

    // Each heartbeat brings a new certificate list; certificates that were not there read bold.
    private OmniDataGrid<CertbotCertificateDto>? _grid;
    private readonly LiveGridRows _liveRows = new();

    protected override Task OnParametersSetAsync() => _liveRows.ObserveAsync(_grid, Server.Certbot, ServerId);

    /// <summary>The Domains column filter (R-121, inline row): a certificate passes when one of its names contains the text.</summary>
    internal static bool DomainsMatch(CertbotCertificateDto cert, string filter) =>
        cert.Domains.Any(domain => domain.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private List<string> CertbotResourceNames => Certbot.Certificates.Select(c => c.Name).ToList();

    // Recette R-69: the horizon is the longest lead time of the certificate expiry alerts, 30 days until the
    // platform has answered (or when no alert is configured).
    private int _warningDays = CertificateAlertLeadTimes.FallbackWarningDays;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _warningDays = await Api.ServerTools.GetCertbotExpiryWarningDaysAsync();
        }
        catch (HttpRequestException)
        {
            // Mandatory: the mark is a hint; a failed lookup keeps the fixed horizon and must not blank the page.
        }
    }

    // Time-dependent, so computed here (browser-local) rather than baked into the DTO contract.
    // ExpiryDate deserializes as Kind=Local in the WASM client, so compare against DateTime.Now.
    private bool IsExpiringSoon(CertbotCertificateDto cert) => cert.ExpiryDate <= DateTime.Now.AddDays(_warningDays);

    // The agent's last renewal rehearsal and per-certificate convention verdict.
    private OmniTone RenewalCheckStyle => Certbot.RenewalCheckSucceeded switch
    {
        true => OmniTone.Success,
        false => OmniTone.Danger,
        null => OmniTone.Neutral
    };

    private string RenewalCheckText => Certbot is { RenewalCheckedAt: { } checkedAt, RenewalCheckSucceeded: { } succeeded }
        ? string.Format(L[succeeded ? "CertbotRenewalCheckPassed" : "CertbotRenewalCheckFailed"], checkedAt.ToString("g"))
        : L["CertbotRenewalCheckNever"];

    // Recette R1-7: the raw output stands in for the reason when no certificate was named, or when one was
    // named without a reason.
    private bool ShowOutputTail => Certbot.RenewalCheckOutputTail.Count > 0
        && (Certbot.RenewalCheckFailures.Count == 0 || Certbot.RenewalCheckFailures.Any(failure => failure.Reason.Length == 0));

    // Recette R-19: the "test the failed ones again" action is offered while the last test left a failed certificate
    // that still exists (the server applies the same rule when it builds the task).
    private bool HasFailedCertificates => Certbot.RenewalCheckSucceeded == false
        && Certbot.RenewalCheckFailures.Any(failure => Certbot.Certificates.Any(c => c.Name == failure.Name));

    // Recette R-19: the helper of an older release ignores certificate names and would test every certificate; the server
    // refuses such a request too, so the targeted tests are not offered as available to it.
    private bool HelperTakesCertificateNames => Certbot.HelperVersion >= CertbotHelperVersion.TargetedRenewalCheck;

    private string OutputTailText => string.Join('\n', Certbot.RenewalCheckOutputTail);

    /// <summary>The condition the helper writes before a proxying rule it can protect; the fix the box shows.</summary>
    internal const string AcmeChallengeCondition = @"RewriteCond %{REQUEST_URI} !^/\.well-known/acme-challenge/";

    /// <summary>One Apache file and the lines of the proxying rules the helper reported in it.</summary>
    internal sealed record AcmeProxyRuleFile(string Path, IReadOnlyList<string> Lines);

    // Recette R-11: the helper records "<file>:<line>" per rule; the box says it once per file.
    private IReadOnlyList<AcmeProxyRuleFile> AcmeProxyRuleFiles => GroupAcmeProxyRules(Certbot.AcmeProxyRules);

    internal static IReadOnlyList<AcmeProxyRuleFile> GroupAcmeProxyRules(IEnumerable<string> rules) =>
    [
        .. rules.Select(SplitRule)
            .GroupBy(rule => rule.Path, StringComparer.Ordinal)
            .Select(group => new AcmeProxyRuleFile(group.Key,
                [.. group.Select(rule => rule.Line).OfType<string>().Distinct(StringComparer.Ordinal)]))
    ];

    private static (string Path, string? Line) SplitRule(string rule)
    {
        var colon = rule.LastIndexOf(':');
        return colon > 0 && colon < rule.Length - 1 && rule[(colon + 1)..].All(char.IsAsciiDigit)
            ? (rule[..colon], rule[(colon + 1)..])
            : (rule, null);
    }

    private string AcmeProxyRuleLine(AcmeProxyRuleFile file) => file.Lines.Count == 0
        ? string.Format(L["CertbotAcmeProxyRuleFile"], file.Path)
        : string.Format(L[file.Lines.Count == 1 ? "CertbotAcmeProxyRuleFileLine" : "CertbotAcmeProxyRuleFileLines"],
            file.Path, string.Join(", ", file.Lines));

    // A helper from before version 6 still reports the rules reserved to websocket upgrades.
    private string AcmeProxyRulesNote => Certbot.HelperVersion < CertbotHelperVersion.Current
        ? L["CertbotAcmeProxyRulesNoteOutdated"]
        : L["CertbotAcmeProxyRulesNote"];

    private static OmniTone ConventionStyle(CertbotCertificateDto cert) => cert.RenewalConvention switch
    {
        CertbotRenewalConvention.Conforming => OmniTone.Success,
        CertbotRenewalConvention.NotWebroot => OmniTone.Danger,
        CertbotRenewalConvention.WrongWebroot => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    private string ConventionText(CertbotCertificateDto cert) => cert.RenewalConvention switch
    {
        CertbotRenewalConvention.Conforming => L["CertbotConventionConforming"],
        CertbotRenewalConvention.NotWebroot => string.Format(L["CertbotConventionNotWebroot"], cert.Authenticator),
        CertbotRenewalConvention.WrongWebroot => L["CertbotConventionWrongWebroot"],
        _ => L["CertbotConventionUnknown"]
    };

    private static string ConventionDetail(CertbotCertificateDto cert) =>
        string.IsNullOrEmpty(cert.WebrootPath) ? cert.Authenticator : $"{cert.Authenticator}: {cert.WebrootPath}";

    // R-56, R-19: every confirmed action here goes through ExecuteActionAsync, which the follower follows in one progress
    // block (a one-off action veils nothing); no page-wide veil either.
    protected override bool VeilsConfirmedActionOnPage => false;

    private async Task ExecuteActionAsync(CertbotAction action, string? certName = null, string? domainName = null)
    {
        await ExecuteServerActionAsync(() => Api.ServerTools.ExecuteCertbotActionAsync(
            ServerId, new CertbotActionRequest
            {
                Action = action,
                CertificateName = certName,
                DomainName = domainName
            }), FollowTitle(action, certName, domainName));
    }

    // Recette R3-37: what the follower calls the task this action queues.
    private string FollowTitle(CertbotAction action, string? certName, string? domainName) => action switch
    {
        CertbotAction.Normalize => L["CertbotNormalize"],
        CertbotAction.RenewalCheck => certName is null ? L["CertbotRenewalCheck"] : $"{L["CertbotRenewalCheck"]} {certName}",
        CertbotAction.RenewalCheckFailed => L["CertbotRetestFailed"],
        CertbotAction.RenewAll => L["RenewAllCerts"],
        CertbotAction.Renew => $"{L["Renew"]} {certName}",
        CertbotAction.RevokeAndDelete => $"{L["Revoke"]} {certName}",
        CertbotAction.RemoveDomain => string.Format(L["CertbotRemoveDomainFollow"], domainName, certName),
        _ => L[action.ToString()]
    };

    /// <summary>Recette R-12: the name a task found again on arrival ("Certbot RenewalCheck - a.example.org", as the backend
    /// names it) carries for the user, the one the page gives the task it launches itself. A name this page does not know
    /// is left to the follower, which shows it as it is.</summary>
    internal string? AdoptedTitle(string taskName)
    {
        var rest = taskName.StartsWith(ServerTaskNames.CertbotPrefix, StringComparison.OrdinalIgnoreCase)
            ? taskName[ServerTaskNames.CertbotPrefix.Length..].Trim()
            : taskName;
        var parts = rest.Split(" - ", 3, StringSplitOptions.TrimEntries);
        var verb = parts[0].Split(' ')[0];
        var certName = parts.Length > 1 ? parts[1] : null;
        if (verb.Equals("create", StringComparison.OrdinalIgnoreCase))
            return parts.Length > 1 ? $"{L["NewCertificate"]} {parts[1]}" : L["NewCertificate"].Value;
        if (!Enum.TryParse<CertbotAction>(verb, out var action)) return null;
        return FollowTitle(action, certName, parts.Length > 2 ? parts[2] : null);
    }

    /// <summary>Recette R-12: a certificate can drop one of its names, never its last one (the helper refuses it
    /// too); certbot reissues it in place, nothing is deleted.</summary>
    internal static IReadOnlyList<string> RemovableDomains(CertbotCertificateDto cert) =>
        cert.Domains.Count < 2 ? [] : cert.Domains;

    private Task ConfirmRemoveDomain(CertbotCertificateDto cert, string domain) => ShowDestructiveConfirm(
        L["CertbotRemoveDomainTitle"], string.Format(L["CertbotRemoveDomainConfirm"], domain, cert.Name),
        () => ExecuteActionAsync(CertbotAction.RemoveDomain, cert.Name, domain), L["CertbotRemoveDomainVerb"].Value,
        OmniIconName.Delete);

    private async Task OpenCreateDialog()
    {
        // SITE-OFFLINE-001: the dialog queues the request itself.
        if (BlockedByOfflineAgent()) return;
        await Dialog.OpenAsync<ServerCertbotCreateDialog>(
            L["NewCertificate"],
            new Dictionary<string, object?> { { "ServerId", ServerId }, { "Follower", _follower } },
            new OmniDialogOptions { Width = "32rem", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
    }


    /// <summary>The export writes the domains as the cell does.</summary>
    private static readonly Func<CertbotCertificateDto, object?> DomainsExport = certificate => string.Join(", ", certificate.Domains);
}
