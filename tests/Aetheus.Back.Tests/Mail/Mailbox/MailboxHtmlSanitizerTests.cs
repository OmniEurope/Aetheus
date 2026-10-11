// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail.Mailbox;

namespace Aetheus.Back.Tests;

/// <summary>Recette R1-17: the HTML of a received message is made safe on the backend before the page shows it.</summary>
public sealed class MailboxHtmlSanitizerTests
{
    private static readonly Dictionary<string, string> s_noInline = [];

    private static MailboxSanitizedHtml Sanitize(string html, IReadOnlyDictionary<string, string>? remote = null) =>
        MailboxHtmlSanitizer.Sanitize(html, s_noInline, remote);

    [Fact]
    public void Script_tags_and_their_content_are_removed()
    {
        var result = Sanitize("<p>Hello</p><script>alert('x')</script>");

        Assert.DoesNotContain("script", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", result.Html, StringComparison.Ordinal);
        Assert.Contains("Hello", result.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<img src=\"data:image/png;base64,AAAA\" onerror=\"alert(1)\">")]
    [InlineData("<a href=\"https://example.org\" onclick=\"alert(1)\">x</a>")]
    [InlineData("<body onload=\"alert(1)\"><p>x</p></body>")]
    [InlineData("<div onmouseover=\"alert(1)\">x</div>")]
    public void Event_handler_attributes_are_removed(string html)
    {
        var result = Sanitize(html);

        Assert.DoesNotContain("alert", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(" on", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">x</a>")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>")]
    public void Dangerous_link_schemes_lose_their_target(string html)
    {
        var result = Sanitize(html);

        Assert.DoesNotContain("javascript:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", "iframe")]
    [InlineData("<form action=\"https://evil.example\"><input name=\"p\"></form>", "form")]
    [InlineData("<object data=\"x.swf\"></object>", "object")]
    [InlineData("<embed src=\"x.swf\">", "embed")]
    [InlineData("<style>body{display:none}</style><p>x</p>", "style")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://evil.example/a.css\">", "link")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">", "meta")]
    [InlineData("<base href=\"https://evil.example/\">", "base")]
    public void Active_or_page_altering_elements_are_removed(string html, string tag)
    {
        var result = Sanitize(html);

        Assert.DoesNotContain("<" + tag, result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Positioning_and_the_page_own_classes_and_ids_are_removed()
    {
        var result = Sanitize("<div id=\"app\" class=\"omni-dialog\" name=\"x\" style=\"position:fixed;z-index:9999;color:red\">x</div>");

        Assert.DoesNotContain("position", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("z-index", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("omni-dialog", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=", result.Html, StringComparison.Ordinal);
        Assert.Contains("color", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_images_are_blocked_by_default_and_listed()
    {
        var result = Sanitize("<img src=\"https://tracker.example/pixel.gif\"><img src=\"http://cdn.example/a.png\">");

        Assert.DoesNotContain("tracker.example", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example", result.Html, StringComparison.Ordinal);
        Assert.Equal(["https://tracker.example/pixel.gif", "http://cdn.example/a.png"], result.BlockedRemoteUrls);
    }

    [Theory]
    [InlineData("<div style=\"background-image:url('https://tracker.example/bg.png')\">x</div>")]
    [InlineData("<img src=\"//tracker.example/pixel.gif\">")]
    public void Remote_resources_outside_img_src_are_blocked_too(string html)
    {
        var result = Sanitize(html);

        Assert.DoesNotContain("tracker.example", result.Html, StringComparison.Ordinal);
        Assert.Single(result.BlockedRemoteUrls);
    }

    [Fact]
    public void The_legacy_background_attribute_is_dropped()
    {
        var result = Sanitize("<table background=\"https://tracker.example/bg.png\"><tr><td>x</td></tr></table>");

        Assert.DoesNotContain("tracker.example", result.Html, StringComparison.Ordinal);
        Assert.Contains(">x<", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fetched_remote_image_is_drawn_from_its_data_url()
    {
        const string url = "https://cdn.example/a.png";
        var result = Sanitize($"<img src=\"{url}\">",
            new Dictionary<string, string> { [url] = "data:image/png;base64,iVBORw0KGgo=" });

        Assert.Contains("data:image/png;base64,iVBORw0KGgo=", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example", result.Html, StringComparison.Ordinal);
        Assert.Empty(result.BlockedRemoteUrls);
    }

    [Fact]
    public void An_inline_cid_image_is_drawn_from_the_message_part()
    {
        var inline = new Dictionary<string, string> { ["logo@x"] = "data:image/png;base64,AAAA" };

        var result = MailboxHtmlSanitizer.Sanitize("<img src=\"cid:logo@x\"><img src=\"cid:missing@x\">", inline);

        Assert.Contains("data:image/png;base64,AAAA", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("cid:", result.Html, StringComparison.Ordinal);
        Assert.Empty(result.BlockedRemoteUrls);
    }

    [Fact]
    public void Svg_data_images_are_refused_raster_ones_kept()
    {
        Assert.True(MailboxHtmlSanitizer.IsSafeImageData("data:image/png;base64,AAAA"));
        Assert.False(MailboxHtmlSanitizer.IsSafeImageData("data:image/svg+xml;base64,PHN2Zz4="));
        Assert.False(MailboxHtmlSanitizer.IsSafeImageData("data:text/html;base64,PHNjcmlwdD4="));
    }

    [Fact]
    public void Links_open_in_a_new_tab_without_referrer()
    {
        var result = Sanitize("<a href=\"https://example.org/page\">x</a><a href=\"mailto:a@example.org\">m</a>");

        Assert.Contains("href=\"https://example.org/page\"", result.Html, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", result.Html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", result.Html, StringComparison.Ordinal);
        Assert.Contains("mailto:a@example.org", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToText_draws_readable_text_from_html()
    {
        var text = MailboxHtmlSanitizer.ToText("<html><head><style>p{}</style></head><body><p>Hello</p><p>World<br>again</p><script>x()</script></body></html>");

        Assert.Equal("Hello\n\nWorld\nagain", text);
    }
}
