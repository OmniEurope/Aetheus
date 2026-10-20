// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>What the mail DNS summary says.</summary>
internal enum DnsSummaryState { None, NothingToChange, Incomplete, ToPublish }

/// <summary>
/// Recette R3-48: the summary of the DNS lines of the mail status. The records the DNS provider holds are
/// the lines that carry a record (A, MX, SPF, DKIM, DMARC), the reverse DNS excepted (the host sets it). A
/// lookup that could not answer leaves the summary incomplete rather than claiming nothing changes.
/// </summary>
public partial class MailDnsSummary
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The lines of the mail status, in fix order.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<MailDiagnosticItemDto> Items { get; set; } = [];

    private IEnumerable<MailDiagnosticItemDto> ProviderRecords => Items.Where(item =>
        item.RecordType.Length > 0 && !string.Equals(item.RecordType, "PTR", StringComparison.OrdinalIgnoreCase));

    /// <summary>The records to publish at the DNS provider, those whose line is red.</summary>
    internal IReadOnlyList<MailDiagnosticItemDto> ToPublish =>
        [.. ProviderRecords.Where(item => item.Verdict is not (MailCheckVerdict.Ok or MailCheckVerdict.Unknown))];

    internal DnsSummaryState State
    {
        get
        {
            var records = ProviderRecords.ToList();
            if (records.Count == 0) return DnsSummaryState.None;
            if (ToPublish.Count > 0) return DnsSummaryState.ToPublish;
            return records.Any(item => item.Verdict == MailCheckVerdict.Unknown)
                ? DnsSummaryState.Incomplete
                : DnsSummaryState.NothingToChange;
        }
    }
}
