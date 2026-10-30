// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>STD-FOCUS (user decision 2026-10-28): moving the focus from code is allowed, for instance onto the
/// comment field of the approval page (R-24). The one thing forbidden is focusing the TITLE of a dialog (or any
/// heading of it): a screen reader must announce the title first and focusing it is useless.</summary>
public class NoAutofocusOnOpenAuditTests
{
    /// <summary>A heading or dialog-title element carrying <c>autofocus</c>.</summary>
    private static readonly Regex AutofocusOnTitle =
        new(@"<(?:h[1-6]|OmniHeading|OmniDialogTitle|OmniDialogHeader)\b[^>]*(?<![\w-])autofocus(?![\w-])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>A focus call from C# whose target or id is a title or heading.</summary>
    private static readonly Regex FocusCallOnTitle =
        new(@"\b\w*(?:Title|Heading)\w*(?:\.\w+)?\.FocusAsync\s*\(|InvokeVoidAsync\s*\(\s*""[\w.]*[Ff]ocus[\w]*""\s*,[^;)]*(?:[Tt]itle|[Hh]eading|\bh[1-6]\b)",
            RegexOptions.Compiled);

    /// <summary>A script focus call on a title or heading.</summary>
    private static readonly Regex ScriptFocusOnTitle =
        new(@"(?:[Tt]itle|[Hh]eading|\bh[1-6]\b)[^;\n]*\.focus\s*\(|querySelector\w*\([^)]*(?:title|heading|\bh[1-6]\b)[^)]*\)\s*\??\.focus\s*\(",
            RegexOptions.Compiled);

    private static string FrontRoot => Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");

    [Fact]
    public void NoRazorMarkup_AutofocusesATitle()
    {
        var offenders = RepositoryScan.Enumerate(FrontRoot, "*.razor")
            .Where(file => AutofocusOnTitle.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(FrontRoot, file))
            .ToList();

        Assert.True(offenders.Count == 0, "A dialog title is focused in markup (STD-FOCUS):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoComponent_FocusesATitleFromItsCode()
    {
        var offenders = RepositoryScan.Enumerate(FrontRoot, "*.cs")
            .Where(file => FocusCallOnTitle.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(FrontRoot, file))
            .ToList();

        Assert.True(offenders.Count == 0, "A title is focused by component code (STD-FOCUS):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheLayoutScript_NeverFocusesATitle()
    {
        var layout = File.ReadAllText(Path.Combine(FrontRoot, "wwwroot", "js", "layout.js"));

        Assert.DoesNotMatch(ScriptFocusOnTitle, layout);
    }

    [Theory]
    [InlineData("<OmniHeading Level=\"H2\" autofocus>Title</OmniHeading>")]
    [InlineData("<h2 class=\"dialog-title\" autofocus>Title</h2>")]
    public void TheGuard_RefusesAnAutofocusedTitle(string markup) =>
        Assert.Matches(AutofocusOnTitle, markup);

    [Theory]
    [InlineData("await _dialogTitle.FocusAsync();")]
    [InlineData("await Js.InvokeVoidAsync(\"Aetheus.focusById\", \"dialog-title\");")]
    public void TheGuard_RefusesACodeFocusOnATitle(string code) =>
        Assert.Matches(FocusCallOnTitle, code);

    [Theory]
    [InlineData("document.querySelector('.dialog-title').focus();")]
    public void TheGuard_RefusesAScriptFocusOnATitle(string script) =>
        Assert.Matches(ScriptFocusOnTitle, script);

    [Theory]
    [InlineData("await _comment.FocusAsync();")]
    [InlineData("await Js.InvokeVoidAsync(\"Aetheus.focusById\", \"approval-comment\");")]
    public void TheGuard_AllowsFocusingAField(string code) =>
        Assert.DoesNotMatch(FocusCallOnTitle, code);
}
