// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// The form rule decided in recette R3-6, R3-10 and R3-13, for every form of the front: a required
/// field shows the required mark that OE draws (<c>OmniFormField.Required</c>, or the model's
/// <c>[Required]</c> it follows), never a star written by hand; a field's description sits behind
/// the "?" of <c>OmniFormField.Help</c>, never as text between the label and the control
/// (<c>OmniFormField.Description</c>) or as a muted line under the control; and that "?" is OE's, never
/// a local look-alike (a component named <c>*HelpRow</c>, <c>*HelpIcon</c>... or a question-mark icon
/// drawn by hand in or beside a field).
/// </summary>
public class FormFieldHelpAndRequiredMarkGuardTests
{
    private const string Field =
        @"<OmniFormField\b(?<open>(?:(?:""[^""]*"")|[^>])*)>(?<body>.*?)</OmniFormField>";

    // A line of help text: a small or muted OmniText, a <small>, or an element styled as a hint.
    private const string HintElement =
        @"<(?<tag>OmniText)\b(?:""[^""]*""|[^>""])*?""[^""]*?(?:OmniTextElement\.Small|OmniTextTone\.Muted|omni-u-text-muted)[^""]*""(?:""[^""]*""|[^>""])*>"
        + @"|<(?<tag>small)\b[^>]*>"
        + @"|<(?<tag>p|span|div)\b[^>]*\bclass=""[^""]*\b(?:form-text|text-muted|hint|help-text|field-description)\b[^""]*""[^>]*>";

    // A local component that imitates the "?" of OmniFormField.Help (ParameterHelpRow, FieldHelpIcon...).
    private const string HelpLookAlikeComponent =
        @"<(?!Omni)(?<name>[A-Z]\w*Help(?:Row|Icon|Mark|Button|Tooltip|Tip|Badge))\b";

    // The question-mark icon OE draws for Help, when written by hand.
    private const string QuestionIcon = @"<OmniIcon\b[^>]*?\bOmniIconName\.Question\b[^>]*>";

    // An optional tooltip and span around a hand-made icon, so the icon beside a field is found either way.
    private const string IconWrapperOpen = @"(?:<OmniTooltip\b(?:""[^""]*""|[^>""])*>\s*)?(?:<span\b[^>]*>\s*)?";
    private const string IconWrapperClose = @"(?:</OmniIcon>\s*)?(?:</span>\s*)?(?:</OmniTooltip>\s*)?";

    [Fact]
    public void Forms_UseTheOeRequiredMarkAndTheHelpQuestionMark()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var violations = RepositoryScan.Enumerate(front, "*.razor")
            .SelectMany(file => Scan(File.ReadAllText(file))
                .Select(violation => $"{Path.GetRelativePath(front, file)}:{violation}"))
            .ToList();

        Assert.True(violations.Count == 0,
            "Formulaires (R3-6, R3-10, R3-13) : l'étoile d'un champ obligatoire vient d'OE "
            + "(OmniFormField.Required ou le [Required] du modèle), jamais écrite à la main ; le descriptif "
            + "d'un champ va dans OmniFormField.Help (le « ? » du libellé), jamais en texte sous le champ, "
            + "ni dans OmniFormField.Description, ni derrière un « ? » fait main (composant *HelpRow, "
            + "icône Question à côté du champ) :\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Files that still draw a bare <c>OmniLabel</c> beside a control, with the reason. A filter or toolbar
    /// selector is no form field (no save, so no star and no help). The "form to migrate" entries are
    /// known debt of recette R-30 rule 8: each one must move into an <c>OmniFormField</c> and leave this
    /// list; a new form is never added here.
    /// </summary>
    private static readonly Dictionary<string, string> BareLabelAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Components/Logs/SystemLogs.razor"] = "filter toolbar",
        ["Components/Projects/AppTelemetryPanel.razor"] = "application selector of a read-only view",
        ["Components/Servers/ServerDetailSections/DockerRelationsGraph.razor"] = "graph filter",
        ["Components/Shared/AppMetricsView.razor"] = "chart filters",
        ["Components/Pipelines/PipelineLaunchDialog.razor"] = "form to migrate (R-30 rule 8)",
        ["Components/Servers/ServerDetailSections/DockerImagesTab.razor"] = "form to migrate (R-30 rule 8)",
        ["Components/Servers/ServerDetailSections/ServerPortsentrySection.razor"] = "form to migrate (R-30 rule 8)",
        ["Components/Servers/ServerDetailSections/ServerRkhunterSection.razor"] = "form to migrate (R-30 rule 8)",
        ["Components/Settings/AdminSettings.razor"] = "form to migrate (R-30 rule 8)",
        ["Layout/ServerDetailLayout.razor"] = "form to migrate (R-30 rule 8)",
    };

    [Fact]
    public void Forms_PutEveryLabelledControlInAnOmniFormField()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var violations = RepositoryScan.Enumerate(front, "*.razor")
            .Where(file => !BareLabelAllowList.ContainsKey(Path.GetRelativePath(front, file).Replace('\\', '/')))
            .SelectMany(file => ScanBareLabels(File.ReadAllText(file))
                .Select(violation => $"{Path.GetRelativePath(front, file)}:{violation}"))
            .ToList();

        Assert.True(violations.Count == 0,
            "Formulaires (R-30 règle 8) : un contrôle libellé est dans un OmniFormField (étoile Required, « ? » Help), "
            + "jamais un OmniLabel nu à côté de lui :\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void BareLabelAllowList_NamesOnlyFilesThatStillHaveABareLabel()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var stale = BareLabelAllowList.Keys
            .Where(path => !File.Exists(Path.Combine(front, path))
                           || ScanBareLabels(File.ReadAllText(Path.Combine(front, path))).Count == 0)
            .ToList();

        Assert.True(stale.Count == 0, "Entrées de la liste d'exceptions sans OmniLabel nu, à retirer :\n  " + string.Join("\n  ", stale));
    }

    [Theory]
    [InlineData("<OmniStack>\n  <OmniLabel For=\"a\">A</OmniLabel>\n  <OmniTextBox Id=\"a\" />\n</OmniStack>", 2)]
    [InlineData("<OmniFormField Label=\"A\" For=\"a\"><OmniTextBox Id=\"a\" /></OmniFormField>\n<OmniLabel For=\"b\">B</OmniLabel>", 2)]
    public void ScanBareLabels_CatchesALabelOutsideAnOmniFormField(string source, int expectedLine)
    {
        Assert.Equal([$"{expectedLine}: bare OmniLabel beside a control; wrap the control in an OmniFormField (Label, Required, Help)"],
            ScanBareLabels(source));
    }

    [Theory]
    [InlineData("<OmniFormField For=\"a\"><LabelContent><OmniLabel For=\"a\">A</OmniLabel></LabelContent><ChildContent><OmniTextBox Id=\"a\" /></ChildContent></OmniFormField>")]
    [InlineData("@* <OmniLabel For=\"a\">A</OmniLabel> *@\n<OmniFormField Label=\"A\" For=\"a\"><OmniTextBox Id=\"a\" /></OmniFormField>")]
    public void ScanBareLabels_AcceptsAFieldAndAComment(string source)
    {
        Assert.Empty(ScanBareLabels(source));
    }

    /// <summary>The <c>OmniLabel</c> elements of one Razor source that sit outside any <c>OmniFormField</c>.</summary>
    internal static IReadOnlyList<string> ScanBareLabels(string source)
    {
        var code = MaskRazorComments(source);
        var withoutFields = Regex.Replace(code, Field,
            field => Regex.Replace(field.Value, @"[^\r\n]", " "), RegexOptions.Singleline);
        return Regex.Matches(withoutFields, @"<OmniLabel\b")
            .Select(label => $"{LineOf(withoutFields, label.Index)}: bare OmniLabel beside a control; wrap the control in an OmniFormField (Label, Required, Help)")
            .ToList();
    }

    [Theory]
    [InlineData("<OmniFormField Label=\"Nom\" Description=\"Le nom affiché\" For=\"n\"><OmniTextBox Id=\"n\" /></OmniFormField>", "Description")]
    [InlineData("<OmniFormField Label=\"Hôtes\" For=\"h\">\n  <OmniTextArea Id=\"h\" />\n  <OmniText Element=\"OmniTextElement.Small\" Tone=\"OmniTextTone.Muted\">@L[\"HostsHelp\"]</OmniText>\n</OmniFormField>", "help text under the control")]
    [InlineData("<OmniFormField For=\"h\"><LabelContent>Hôtes</LabelContent><ChildContent>\n  <OmniTextBox Id=\"h\" />\n  <small>Un par ligne</small>\n</ChildContent></OmniFormField>", "help text under the control")]
    [InlineData("<OmniFormField Label=\"Canal\" For=\"c\"><OmniTextBox Id=\"c\" /></OmniFormField>\n<OmniText Element=\"OmniTextElement.Small\" Tone=\"OmniTextTone.Muted\" Class=\"omni-u-text-muted\">@L[\"ChannelHint\"]</OmniText>", "help text right under the field")]
    [InlineData("<OmniFormField Label=\"Nom *\" For=\"n\"><OmniTextBox Id=\"n\" /></OmniFormField>", "hand-written required mark")]
    [InlineData("<OmniLabel For=\"@p.Name\">@($\"{label}{(p.Required ? \" *\" : \"\")}\")</OmniLabel>", "hand-written required mark")]
    [InlineData("<label for=\"n\">Nom <span class=\"req\">*</span></label>", "hand-written required mark")]
    // The local "?" the template parameters used before OmniFormField.Help.
    [InlineData("<ParameterHelpRow Description=\"@help\" DescriptionId=\"@id\">\n  <OmniFormField Label=\"Cible\" For=\"t\"><OmniTextBox Id=\"t\" /></OmniFormField>\n</ParameterHelpRow>", "local help look-alike ParameterHelpRow")]
    [InlineData("<OmniFormField Label=\"Cible\" For=\"t\"><OmniTextBox Id=\"t\" /></OmniFormField><FieldHelpIcon Text=\"@help\" />", "local help look-alike FieldHelpIcon")]
    [InlineData("<OmniFormField For=\"t\"><LabelContent>Cible <OmniTooltip Text=\"@help\"><OmniIcon Name=\"OmniIconName.Question\" /></OmniTooltip></LabelContent><ChildContent><OmniTextBox Id=\"t\" /></ChildContent></OmniFormField>", "hand-made help icon in an OmniFormField")]
    [InlineData("<OmniFormField Label=\"Cible\" For=\"t\"><OmniTextBox Id=\"t\" /></OmniFormField>\n<OmniTooltip Text=\"@help\" Focusable=\"true\">\n  <span role=\"img\" aria-label=\"@help\"><OmniIcon Name=\"OmniIconName.Question\" aria-hidden=\"true\" /></span>\n</OmniTooltip>", "hand-made help icon beside an OmniFormField")]
    [InlineData("<OmniTooltip Text=\"@help\"><OmniIcon Name=\"OmniIconName.Question\" /></OmniTooltip>\n<OmniFormField Label=\"Cible\" For=\"t\"><OmniTextBox Id=\"t\" /></OmniFormField>", "hand-made help icon beside an OmniFormField")]
    public void Scan_CatchesEachKindOfViolation(string source, string expected)
    {
        var violations = Scan(source);

        Assert.Contains(violations, violation => violation.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>One hint must not swallow the next field: each muted line under a field is reported.</summary>
    [Fact]
    public void Scan_ReportsEveryHintOfAFormWithSeveralFields()
    {
        const string source =
            "<OmniFormField Label=\"@L[\"Type\"]\" For=\"t\"><OmniDropDown Id=\"t\" /></OmniFormField>\n"
            + "<OmniText Element=\"OmniTextElement.Small\" Tone=\"OmniTextTone.Muted\" Class=\"omni-u-text-muted\">\n"
            + "    @L[\"TypeHint\"]\n</OmniText>\n"
            + "<OmniFormField For=\"u\"><LabelContent>@L[\"Url\"]</LabelContent><ChildContent><OmniTextBox Id=\"u\" /></ChildContent></OmniFormField>\n"
            + "<OmniText Element=\"OmniTextElement.Small\" Tone=\"OmniTextTone.Muted\">@L[\"UrlHint\"]</OmniText>";

        Assert.Equal(["1: help text right under the field; use OmniFormField.Help",
                      "5: help text right under the field; use OmniFormField.Help"], Scan(source));
    }

    [Theory]
    // The rule's own form: Required and Help on the field.
    [InlineData("<OmniFormField Label=\"Nom\" Required=\"true\" Help=\"@L[\"NameHelp\"]\" For=\"n\"><OmniTextBox Id=\"n\" /></OmniFormField>")]
    // A value shown after the control (a domain suffix) is content, not a description.
    [InlineData("<OmniFormField Label=\"Adresse\" For=\"a\"><ChildContent><OmniTextBox Id=\"a\" /></ChildContent><EndContent><OmniText Tone=\"OmniTextTone.Muted\">@domain</OmniText></EndContent></OmniFormField>")]
    // The empty state of a list the field edits sits above its Add button, not under the control.
    [InlineData("<OmniFormField For=\"add\" Help=\"x\"><LabelContent>Fichiers</LabelContent><ChildContent><OmniStack>@if (none) { <OmniText Class=\"omni-u-text-muted\">Aucun</OmniText> }<OmniButton Id=\"add\" /></OmniStack></ChildContent></OmniFormField>")]
    // The muted title of the next group of fields.
    [InlineData("<OmniFormField Label=\"Tags\" For=\"t\"><OmniTextBox Id=\"t\" /></OmniFormField>\n<OmniText Element=\"OmniTextElement.Strong\" Class=\"omni-u-text-muted\">@L[\"CiSettings\"]</OmniText>")]
    // A Razor comment and a cron placeholder hold stars that are no required mark.
    [InlineData("@* A comment *@\n<OmniFormField Label=\"Planification\" For=\"s\"><OmniTextBox Id=\"s\" Placeholder=\"0 0 */6 * * *\" /></OmniFormField>")]
    // A help panel of a page is no field help, and a question-mark icon away from any field means "unknown".
    [InlineData("<MailClientSettingsHelp ServerId=\"@id\" />\n<OmniCard><OmniIcon Name=\"OmniIconName.Question\" /><OmniText>@L[\"PatchUnknown\"]</OmniText></OmniCard>")]
    public void Scan_AcceptsTheRuleAndWhatIsNotADescription(string source)
    {
        Assert.Empty(Scan(source));
    }

    /// <summary>The violations of one Razor source, each as "line: what".</summary>
    internal static IReadOnlyList<string> Scan(string source)
    {
        var code = MaskRazorComments(source);
        var violations = new List<string>();

        foreach (Match field in Regex.Matches(code, Field, RegexOptions.Singleline))
        {
            if (Regex.IsMatch(field.Groups["open"].Value, @"\bDescription="))
                violations.Add($"{LineOf(code, field.Index)}: OmniFormField.Description shows the description as text; use Help");

            var body = field.Groups["body"];
            foreach (Match icon in Regex.Matches(body.Value, QuestionIcon, RegexOptions.Singleline))
                violations.Add($"{LineOf(code, body.Index + icon.Index)}: hand-made help icon in an OmniFormField; use OmniFormField.Help");

            foreach (Match hint in Regex.Matches(body.Value, HintElement, RegexOptions.Singleline))
            {
                if (IsHeading(hint.Value)) continue;
                var close = body.Value.IndexOf($"</{hint.Groups["tag"].Value}>", hint.Index, StringComparison.Ordinal);
                if (close < 0) continue;
                var rest = body.Value[(close + hint.Groups["tag"].Value.Length + 3)..];
                if (Regex.IsMatch(rest, @"^\s*(?:</ChildContent>\s*)?$"))
                    violations.Add($"{LineOf(code, body.Index + hint.Index)}: help text under the control of an OmniFormField; use Help");
            }
        }

        foreach (Match after in Regex.Matches(code, @"</OmniFormField>\s*(?:" + HintElement + ")", RegexOptions.Singleline))
            if (!IsHeading(after.Value))
                violations.Add($"{LineOf(code, after.Index)}: help text right under the field; use OmniFormField.Help");

        foreach (Match beside in Regex.Matches(code,
                     @"</OmniFormField>\s*" + IconWrapperOpen + QuestionIcon
                     + "|" + QuestionIcon + @"\s*" + IconWrapperClose + @"<OmniFormField\b", RegexOptions.Singleline))
            violations.Add($"{LineOf(code, beside.Index)}: hand-made help icon beside an OmniFormField; use OmniFormField.Help");

        foreach (Match lookAlike in Regex.Matches(code, HelpLookAlikeComponent))
            violations.Add($"{LineOf(code, lookAlike.Index)}: local help look-alike {lookAlike.Groups["name"].Value}; use OmniFormField.Help");

        foreach (Match star in Regex.Matches(code,
                     @"\bLabel=""[^""]*\*\s*""|>\s*\*\s*<|""\s*\*\s*""|&#42;|&ast;"))
            violations.Add($"{LineOf(code, star.Index)}: hand-written required mark; use OmniFormField.Required or the model's [Required]");

        return violations;
    }

    // A strong muted line under a field opens the next group of the form: a title, not a description.
    private static bool IsHeading(string element) => element.Contains("OmniTextElement.Strong", StringComparison.Ordinal);

    private static string MaskRazorComments(string source) =>
        Regex.Replace(source, @"@\*.*?\*@",
            comment => Regex.Replace(comment.Value, @"[^\r\n]", " "), RegexOptions.Singleline);

    private static int LineOf(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;
}
