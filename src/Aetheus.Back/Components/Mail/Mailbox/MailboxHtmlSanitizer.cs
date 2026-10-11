// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// Recette R1-17. The HTML of a received message, made safe before it leaves the backend: no script,
/// no event handler, no form, no frame, no style sheet, nothing positioned over the page. A link opens
/// in a new tab without referrer. An image is shown only from data the message itself carries (inline
/// <c>cid:</c> parts and <c>data:image</c>) or from <paramref name="remoteImages"/>, the remote images
/// the reader asked to see, already fetched by the backend; every other remote resource (image source,
/// CSS <c>url()</c>) is removed and listed in
/// <see cref="MailboxSanitizedHtml.BlockedRemoteUrls"/>. The legacy <c>background</c> attribute is not allowed at all.
/// </summary>
public static partial class MailboxHtmlSanitizer
{
    private static readonly string[] s_removedTags =
        ["form", "input", "button", "select", "option", "optgroup", "textarea", "datalist", "keygen", "output", "menuitem"];

    private static readonly string[] s_removedCss = ["position", "z-index"];

    private static readonly string[] s_removedAttributes = ["srcset", "id", "class", "name"];

    public static MailboxSanitizedHtml Sanitize(
        string html,
        IReadOnlyDictionary<string, string> inlineImages,
        IReadOnlyDictionary<string, string>? remoteImages = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(inlineImages);
        var blocked = new List<string>();
        var sanitizer = new HtmlSanitizer();
        foreach (var tag in s_removedTags) sanitizer.AllowedTags.Remove(tag);
        foreach (var property in s_removedCss) sanitizer.AllowedCssProperties.Remove(property);
        // No srcset (a second, unfiltered source list); no id, class or name, which would let the message
        // take the page's own styles or clobber a named element of the page.
        foreach (var attribute in s_removedAttributes) sanitizer.AllowedAttributes.Remove(attribute);
        sanitizer.AllowedSchemes.Add("mailto");

        sanitizer.FilterUrl += (_, args) =>
            args.SanitizedUrl = FilterUrl(args.Tag, args.OriginalUrl, inlineImages, remoteImages, blocked);
        sanitizer.PostProcessNode += (_, args) =>
        {
            if (args.Node is IElement { LocalName: "a" } anchor && anchor.HasAttribute("href"))
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer");
            }
        };

        var result = sanitizer.Sanitize(html);
        return new MailboxSanitizedHtml(result, blocked.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>The readable text of an HTML body, for the quote of a reply when the message has no text part.</summary>
    public static string ToText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = new HtmlParser().ParseDocument(html);
        foreach (var removed in document.QuerySelectorAll("script, style, head").ToList()) removed.Remove();
        foreach (var lineBreak in document.QuerySelectorAll("br").ToList()) lineBreak.Replace(document.CreateTextNode("\n"));
        foreach (var block in document.QuerySelectorAll("div, li, tr").ToList())
            block.Append(document.CreateTextNode("\n"));
        // A paragraph or a heading ends with a blank line, as in a text part.
        foreach (var paragraph in document.QuerySelectorAll("p, h1, h2, h3, h4, h5, h6").ToList())
            paragraph.Append(document.CreateTextNode("\n\n"));
        var text = document.Body?.TextContent ?? string.Empty;
        return BlankLines().Replace(HorizontalSpace().Replace(text, " "), "\n\n").Trim();
    }

    private static string? FilterUrl(
        IElement element,
        string originalUrl,
        IReadOnlyDictionary<string, string> inlineImages,
        IReadOnlyDictionary<string, string>? remoteImages,
        List<string> blocked)
    {
        var url = originalUrl.Trim();
        // A link the reader clicks: web and mail addresses only. Anything else on an anchor is a
        // resource (a CSS url() in its style) and falls through to the resource rules.
        if (element.LocalName is "a" or "area"
            && string.Equals(element.GetAttribute("href")?.Trim(), url, StringComparison.Ordinal))
            return IsScheme(url, "http") || IsScheme(url, "https") || IsScheme(url, "mailto") ? url : null;

        if (IsScheme(url, "cid"))
        {
            var contentId = url[4..].Trim('<', '>');
            return inlineImages.TryGetValue(contentId, out var inline) ? inline : null;
        }
        if (IsSafeImageData(url)) return url;
        if (IsScheme(url, "http") || IsScheme(url, "https") || url.StartsWith("//", StringComparison.Ordinal))
        {
            if (remoteImages is not null && remoteImages.TryGetValue(url, out var fetched)) return fetched;
            blocked.Add(url);
        }
        return null;
    }

    /// <summary>A raster image carried in the URL itself (never SVG, which can hold script).</summary>
    public static bool IsSafeImageData(string url) => SafeImageData().IsMatch(url);

    private static bool IsScheme(string url, string scheme) =>
        url.Length > scheme.Length && url[scheme.Length] == ':'
        && url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^data:image/(png|jpeg|gif|webp|bmp);base64,[A-Za-z0-9+/=\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeImageData();

    [GeneratedRegex(@"[ \t\f\v ]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"\s*\n\s*\n\s*")]
    private static partial Regex BlankLines();
}
