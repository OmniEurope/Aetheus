// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>A page body that fills the window (a full-height grid, a page-fill-body such as the template update
/// diff) keeps its bottom padding inside the window's height: otherwise the page scrolls by that padding on top of
/// the grid or diff that already scrolls (R-30 page-scroll-with-grid, R-100: 12 px measured on the update page).</summary>
public sealed class PageFillWrapperBoxSizingTests
{
    private static readonly string Css = Regex.Replace(
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")),
        @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    [Theory]
    [InlineData(@".app-body-wrapper:has(> .aetheus-grid-host > .aetheus-grid-fullheight)")]
    [InlineData(@".app-body-wrapper:has(> .page-fill-body)")]
    public void AFillingPageBody_CountsItsPaddingInsideTheWindowHeight(string selector)
    {
        var rule = Regex.Match(Css, Regex.Escape(selector) + @"\s*\{(?<body>[^{}]*)\}");
        Assert.True(rule.Success, $"rule {selector} not found in app.css");
        Assert.Matches(@"box-sizing\s*:\s*border-box", rule.Groups["body"].Value);
    }
}
