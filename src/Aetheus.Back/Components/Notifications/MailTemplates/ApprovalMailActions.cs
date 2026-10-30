// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Services.Email;
using static Aetheus.Back.Services.Email.EmailHtmlLayout;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// Recette R-24: the Approve, Reject and Comment buttons at the top of an approval request mail. Each one is a
/// plain link to the approval's page in the application, never a signed or tokenised address: opening it
/// changes nothing (mail scanners and link previews fetch links). The page decides through the authenticated
/// API, once, and only for a reader already signed in; a reader who must sign in first gets the page without
/// any decision taken.
/// </summary>
internal static class ApprovalMailActions
{
    /// <summary>The query parameter the page reads its one-shot action from; the front's sign-in drops it.</summary>
    public const string ActionParameter = "action";

    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Comment = "comment";

    // Solid fills with white words (contrast 5.7:1 and 5.6:1), readable on a light or a dark mail alike:
    // Primary blue and Danger red, the colours of the page's own Approve and Reject buttons.
    private const string PrimaryFill = "#1565c0";
    private const string DangerFill = "#c62828";
    private const string OnFill = "#ffffff";

    /// <summary>The application path of an approval's page with an action; the page acts on it (see the class summary).</summary>
    public static string Path(int runId, int approvalId, string action) =>
        $"/pipelines/runs/{runId}/approvals/{approvalId}?{ActionParameter}={action}";

    /// <summary>
    /// The three buttons as a table of presentation (mail clients render no CSS button), or empty when the
    /// mail is no approval request, names no approval, or has no web address to link to.
    /// </summary>
    public static string Block(string eventType, int? runId, int? approvalId, string? frontOrigin, MailBlockColours colours, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(colours);
        ArgumentNullException.ThrowIfNull(text);
        if (eventType != "pipeline.approval-requested" || runId is not { } run || approvalId is not { } approval
            || Url(frontOrigin, Path(run, approval, Approve)) is null)
            return string.Empty;
        // Comment is Secondary: the neutral pair of the recipient's mode.
        var (secondaryFill, secondaryInk) = colours.Pair(MailTone.Neutral);
        var html = new StringBuilder("<table><tr>");
        Button(html, Url(frontOrigin, Path(run, approval, Approve))!, text("ApprovalActionApprove"), PrimaryFill, OnFill);
        Button(html, Url(frontOrigin, Path(run, approval, Reject))!, text("ApprovalActionReject"), DangerFill, OnFill);
        Button(html, Url(frontOrigin, Path(run, approval, Comment))!, text("ApprovalActionComment"), secondaryFill, secondaryInk);
        return html.Append("</tr></table>").ToString();
    }

    private static void Button(StringBuilder html, string href, string label, string fill, string ink) =>
        html.Append("<td style=\"padding:10px 18px;background-color:").Append(fill).Append(";color:").Append(ink).Append("\">")
            .Append("<a href=\"").Append(Encode(href)).Append("\" style=\"font-size:15px;font-weight:700;color:").Append(ink).Append("\">")
            .Append(Encode(label)).Append("</a></td>");
}
