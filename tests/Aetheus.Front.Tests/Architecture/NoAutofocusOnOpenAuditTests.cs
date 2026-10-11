// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette R1-35, STD-FOCUS: no page or dialog puts the cursor in a field when it opens. Three dialogs
/// (project form, dashboard name, secret value) used to call a <c>focusById</c> script from their first
/// render; the script and its callers are gone, and this guard keeps the ways back out: an
/// <c>autofocus</c> attribute in markup, a <c>FocusAsync</c> call, or a script call whose name moves
/// the focus. The initial focus of an OE dialog is OE's (its close button), not the application's.
/// </summary>
public class NoAutofocusOnOpenAuditTests
{
    private static readonly Regex AutofocusAttribute =
        new(@"(?<![\w-])autofocus(?![\w-])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FocusCall =
        new(@"\.FocusAsync\s*\(|InvokeVoidAsync\s*\(\s*""[\w.]*[Ff]ocus[\w]*""", RegexOptions.Compiled);

    private static readonly Regex ScriptFocus =
        new(@"\.focus\s*\(", RegexOptions.Compiled);

    private static string FrontRoot => Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");

    [Fact]
    public void NoRazorMarkup_CarriesAnAutofocusAttribute()
    {
        var offenders = RepositoryScan.Enumerate(FrontRoot, "*.razor")
            .Where(file => AutofocusAttribute.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(FrontRoot, file))
            .ToList();

        Assert.True(offenders.Count == 0, "autofocus in markup (STD-FOCUS):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoComponent_MovesTheFocusFromItsCode()
    {
        var offenders = RepositoryScan.Enumerate(FrontRoot, "*.cs")
            .Where(file => FocusCall.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(FrontRoot, file))
            .ToList();

        Assert.True(offenders.Count == 0, "Focus moved by component code (STD-FOCUS):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheLayoutScript_HasNoFocusHelperLeft()
    {
        var layout = File.ReadAllText(Path.Combine(FrontRoot, "wwwroot", "js", "layout.js"));

        Assert.DoesNotMatch(ScriptFocus, layout);
    }
}
