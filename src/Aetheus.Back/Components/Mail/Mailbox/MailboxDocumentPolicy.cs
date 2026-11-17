// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// The Content-Security-Policy of a message document, its own and not the interface's: the interface forbids
/// inline style attributes (<c>style-src-attr 'none'</c>), which left most HTML messages without their
/// formatting. Here the inline styles the sanitizer keeps apply, and nothing else loads:
/// <list type="bullet">
/// <item><c>default-src 'none'</c>: no script, font, frame, connection or remote resource of any kind;</item>
/// <item><c>style-src-attr 'unsafe-inline'</c>: the style attributes the sanitizer filtered (no position,
/// no z-index, no url() outside data: images). No <c>style-src</c> or <c>style-src-elem</c> source: the
/// sanitizer removes every &lt;style&gt; and &lt;link&gt; element, so allowing them would only widen the policy;</item>
/// <item><c>img-src data:</c>: inline and fetched images both arrive as data: URLs;</item>
/// <item><c>base-uri 'none'</c> and <c>form-action 'none'</c>: no base URL rewrite, no form post;</item>
/// <item><c>frame-ancestors</c>: only the interface's origins may frame it (none configured: nobody).</item>
/// </list>
/// </summary>
public sealed class MailboxDocumentPolicy
{
    public MailboxDocumentPolicy(IEnumerable<string> interfaceOrigins)
    {
        ArgumentNullException.ThrowIfNull(interfaceOrigins);
        var ancestors = interfaceOrigins
            .Select(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                    ? uri.GetLeftPart(UriPartial.Authority)
                    : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ContentSecurityPolicy =
            "default-src 'none'; " +
            "style-src-attr 'unsafe-inline'; " +
            "img-src data:; " +
            "base-uri 'none'; " +
            "form-action 'none'; " +
            $"frame-ancestors {(ancestors.Count == 0 ? "'none'" : string.Join(' ', ancestors))}";
    }

    public string ContentSecurityPolicy { get; }

    /// <summary>The frame's whole document around the sanitized body; links open in a new tab.</summary>
    public static string Wrap(string sanitizedHtml) =>
        $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><base target=\"_blank\"></head><body>{sanitizedHtml}</body></html>";
}
