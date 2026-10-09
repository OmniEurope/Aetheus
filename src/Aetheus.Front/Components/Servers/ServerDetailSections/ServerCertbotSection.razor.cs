// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerCertbotSection : ServerActionSectionBase
{
    [Inject] private ApiClient Api { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;
    [Parameter, EditorRequired] public int ServerId { get; set; }

    private string _certSearch = string.Empty;

    private CertbotDataDto Certbot => Server.Certbot;

    // Recette R-210: the renewal filter lists the translated conventions.
    private Func<string, string>? _conventionFilterText;
    private Func<string, string> ConventionFilterText => _conventionFilterText ??= GridFilterText.ForEnum<CertbotRenewalConvention>(L);

    // Recette R-227: each heartbeat brings a new certificate list; certificates that were not there read bold.
    private OmniDataGrid<CertbotCertificateDto>? _grid;
    private readonly LiveGridRows _liveRows = new();

    protected override Task OnParametersSetAsync() => _liveRows.ObserveAsync(_grid, Server.Certbot, ServerId);

    private List<CertbotCertificateDto> FilteredCertificates => string.IsNullOrWhiteSpace(_certSearch)
        ? Certbot.Certificates
        : Certbot.Certificates.Where(c =>
            c.Name.Contains(_certSearch, StringComparison.OrdinalIgnoreCase) ||
            c.Domains.Any(d => d.Contains(_certSearch, StringComparison.OrdinalIgnoreCase))).ToList();

    private List<string> CertbotResourceNames => Certbot.Certificates.Select(c => c.Name).ToList();

    // Time-dependent, so computed here (browser-local) rather than baked into the DTO contract.
    // ExpiryDate deserializes as Kind=Local in the WASM client, so compare against DateTime.Now.
    private static bool IsExpiringSoon(CertbotCertificateDto cert) => cert.ExpiryDate <= DateTime.Now.AddDays(30);

    // PLAN-007: last renewal rehearsal and per-certificate convention verdict, as the agent reported them.
    private OmniTone RenewalCheckStyle => Certbot.RenewalCheckSucceeded switch
    {
        true => OmniTone.Success,
        false => OmniTone.Danger,
        null => OmniTone.Neutral
    };

    private string RenewalCheckText => Certbot is { RenewalCheckedAt: { } checkedAt, RenewalCheckSucceeded: { } succeeded }
        ? string.Format(L[succeeded ? "CertbotRenewalCheckPassed" : "CertbotRenewalCheckFailed"], checkedAt.ToString("g"))
        : L["CertbotRenewalCheckNever"];

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

    private async Task ExecuteActionAsync(CertbotAction action, string? certName = null)
    {
        await ExecuteServerActionAsync(() => Api.ServerTools.ExecuteCertbotActionAsync(
            ServerId, new CertbotActionRequest
            {
                Action = action,
                CertificateName = certName
            }));
    }

    private async Task OpenCreateDialog()
    {
        await Dialog.OpenAsync<ServerCertbotCreateDialog>(
            L["NewCertificate"],
            new Dictionary<string, object?> { { "ServerId", ServerId } },
            new OmniDialogOptions { Width = "32rem", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
    }


    /// <summary>Recette R2-124: the export writes the domains as the cell does.</summary>
    private static readonly Func<CertbotCertificateDto, object?> DomainsExport = certificate => string.Join(", ", certificate.Domains);
}
