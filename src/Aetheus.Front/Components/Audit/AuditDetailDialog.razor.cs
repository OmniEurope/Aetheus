// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Audit;

public partial class AuditDetailDialog : ComponentBase
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter] public AuditLogDto Log { get; set; } = default!;

    private bool _verifying = true;
    private AuditChainVerificationResult? _verification;

    // The same label and colour as the audit log row the dialog was opened from.
    private OmniTone ActionBadge => AuditActionPresentation.Badge(Log.Action);

    private string ActionLabel => AuditActionPresentation.Label(L, Log.Action);

    // Pretty-print JSON details so before/after payloads are readable; fall back to the raw string.
    private string FormattedDetails
    {
        get
        {
            var details = Log.Details ?? string.Empty;
            var trimmed = details.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try
                {
                    using var doc = JsonDocument.Parse(details);
                    return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (JsonException) { /* not JSON - show as-is */ }
            }
            return details;
        }
    }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _verification = await Api.Monitoring.VerifyAuditEntryAsync(Log.Id);
        }
        catch (HttpRequestException)
        {
            // Honest "unavailable" rather than an optimistic green.
            _verification = null;
        }
        finally
        {
            _verifying = false;
        }
    }

    private Task CopyAsync() => Clipboard.CopyAsync(Log.Details, L["MessageCopied"]);

    private void Close() => Dialog.Close();
}
