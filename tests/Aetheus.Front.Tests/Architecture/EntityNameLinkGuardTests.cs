// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette R-118 (rule 13 of R-30): the displayed name of a server, project, pipeline, environment, library,
/// vault or organization links to the page of that entity. This guard reads every <c>.razor</c> file and, for
/// each grid column bound to a <c>*ServerName</c>, <c>*ProjectName</c>, <c>*PipelineName</c>,
/// <c>*EnvironmentName</c>, <c>*OrganizationName</c>, <c>*LibraryName</c> or <c>*VaultName</c> property
/// (<c>Property</c> or <c>Key</c>), requires a <c>Template</c> that renders a link: <c>EntityNameLink</c>,
/// <c>OmniLink</c>, <c>NavLink</c> or an <c>&lt;a href&gt;</c>. A column without a Template (plain text) or
/// with a Template that writes the name as text fails.
/// Limits: static, columns only (detail pages and dialogs are not covered); it cannot tell that the link goes
/// to the right entity, and the name of a row whose DTO carries no id goes in the commented allow-list.
/// </summary>
public class EntityNameLinkGuardTests
{
    private static readonly Regex ColumnOpen = new(
        @"<OmniDataGridColumn\b(?<attrs>(?:[^>""]|""[^""]*"")*?)(?<close>/?)>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex LinkedName = new(
        @"\b(?:Property|Key)=""\w*(?:Server|Project|Pipeline|Environment|Organization|Library|Vault)Name""",
        RegexOptions.CultureInvariant);

    private static readonly Regex TItemAttribute = new(@"\bTItem=""(?<type>\w+)""", RegexOptions.CultureInvariant);

    private static readonly Regex LinkMarkup = new(
        @"<EntityNameLink\b|<OmniLink\b|<NavLink\b|<a\s[^>]*\bhref=",
        RegexOptions.CultureInvariant);

    // (row type, property): a *Name column that is not the name of an entity with a page, or whose DTO carries
    // no id to link with. Adding to this list needs the reason, as below.
    private static readonly (string Item, string Property, string Reason)[] AllowList =
    [
        ("ApacheVirtualHostGroup", "ServerName", "the Apache ServerName directive of a virtual host, not a Server entity"),
        ("PipelineArtifactDto", "EnvironmentName", "free-text name stored on the artifact, no environment id in the DTO"),
    ];

    [Fact]
    public void NamedEntityColumns_RenderALink()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var violations = new List<string>();
        var inspected = 0;
        foreach (var file in RepositoryScan.Enumerate(front, "*.razor"))
        {
            var relative = Path.GetRelativePath(front, file).Replace('\\', '/');
            var result = Scan(File.ReadAllText(file), relative);
            inspected += result.Inspected;
            violations.AddRange(result.Violations);
        }

        Assert.True(inspected >= 20, $"The scan found only {inspected} entity-name columns; the pattern no longer matches the grids.");
        Assert.True(violations.Count == 0,
            "A grid column shows the name of a server, project, pipeline, environment, organization, library or vault as plain text. "
            + "Render it with <EntityNameLink> (or add the row type to the allow-list with a reason):\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Scanner_FlagsPlainTextNameColumns_AndAcceptsLinks()
    {
        const string plainSelfClosing = "<OmniDataGridColumn TItem=\"FooDto\" Property=\"ServerName\" Title=\"S\" />";
        const string plainTemplate = "<OmniDataGridColumn TItem=\"FooDto\" Property=\"ProjectName\"><Template Context=\"x\">@x.ProjectName</Template></OmniDataGridColumn>";
        const string linked = "<OmniDataGridColumn TItem=\"FooDto\" Property=\"PipelineName\"><Template Context=\"x\"><EntityNameLink Kind=\"EntityNameKind.Pipeline\" Id=\"x.PipelineId\" Name=\"@x.PipelineName\" /></Template></OmniDataGridColumn>";
        const string allowed = "<OmniDataGridColumn TItem=\"ApacheVirtualHostGroup\" Property=\"ServerName\" />";
        const string unrelated = "<OmniDataGridColumn TItem=\"FooDto\" Property=\"Hostname\" />";

        Assert.Single(Scan(plainSelfClosing, "a.razor").Violations);
        Assert.Single(Scan(plainTemplate, "b.razor").Violations);
        Assert.Empty(Scan(linked, "c.razor").Violations);
        Assert.Empty(Scan(allowed, "d.razor").Violations);
        Assert.Equal(0, Scan(unrelated, "e.razor").Inspected);
        Assert.Equal(1, Scan(linked, "c.razor").Inspected);
    }

    private static (int Inspected, List<string> Violations) Scan(string text, string path)
    {
        var violations = new List<string>();
        var inspected = 0;
        foreach (Match open in ColumnOpen.Matches(text))
        {
            var attrs = open.Groups["attrs"].Value;
            var named = LinkedName.Match(attrs);
            if (!named.Success)
                continue;

            inspected++;
            var item = TItemAttribute.Match(attrs).Groups["type"].Value;
            var property = Regex.Match(named.Value, @"""(?<p>\w+)""").Groups["p"].Value;
            if (AllowList.Any(a => a.Item == item && a.Property == property))
                continue;

            var line = text[..open.Index].Count(c => c == '\n') + 1;
            var body = string.Empty;
            if (open.Groups["close"].Value != "/")
            {
                var end = text.IndexOf("</OmniDataGridColumn>", open.Index, StringComparison.Ordinal);
                body = end < 0 ? text[(open.Index + open.Length)..] : text[(open.Index + open.Length)..end];
            }

            if (!LinkMarkup.IsMatch(body))
                violations.Add($"{path}:{line} column {property} ({item})");
        }

        return (inspected, violations);
    }
}
