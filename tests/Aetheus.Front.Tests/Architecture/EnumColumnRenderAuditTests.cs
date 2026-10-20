// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Enum and boolean cells need templates rendering localized values; filter labels alone
/// do not localize cell contents. The guard resolves property types and rejects direct enum
/// rendering.</summary>
public sealed class EnumColumnRenderAuditTests
{
    private static readonly Regex ColumnOpen = new(@"<OmniDataGridColumn\b", RegexOptions.Compiled);
    private static readonly Regex Attribute = new(@"\b(?<name>[A-Za-z]+)=""(?<value>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex TemplateElement = new(@"<Template\b(?<attributes>[^>]*)>(?<body>.*?)</Template>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, Type[]> TypesByName = new[]
        {
            typeof(Aetheus.Front.Components.Shared.AetheusDataGrid<>).Assembly,
            typeof(Aetheus.Shared.Components.Pipelines.PipelineStatus).Assembly
        }
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => !type.IsGenericTypeDefinition)
        .GroupBy(type => type.Name, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

    [Fact]
    public void An_Enum_Or_Boolean_Column_Renders_Its_Translated_Text()
    {
        var files = RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Components"), "*.razor");
        var offenders = new List<string>();
        var resolved = 0;
        var translatable = 0;

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var column in Columns(text))
            {
                var attributes = Attribute.Matches(column.OpeningTag)
                    .GroupBy(match => match.Groups["name"].Value, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().Groups["value"].Value, StringComparer.Ordinal);
                if (!attributes.TryGetValue("TItem", out var itemName) || !attributes.TryGetValue("Property", out var property))
                    continue;
                var propertyType = PropertyType(itemName, property);
                if (propertyType is null) continue;
                resolved++;

                var valueType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                if (!valueType.IsEnum && valueType != typeof(bool)) continue;
                translatable++;

                var where = $"{Path.GetRelativePath(RepositoryScan.Root, file)}:{column.Line} ({itemName}.{property})";
                var template = TemplateElement.Match(column.Body);
                if (!template.Success && !attributes.ContainsKey("Template"))
                {
                    offenders.Add($"{where}: no Template, the cell shows the raw {valueType.Name} value");
                    continue;
                }
                if (template.Success && WritesRawValue(template, property))
                    offenders.Add($"{where}: the Template writes the raw {valueType.Name} value");
            }
        }

        Assert.True(resolved >= 100, $"Only {resolved} grid columns resolved to a property type; the guard is not seeing the app.");
        Assert.True(translatable >= 10, $"Only {translatable} enum or boolean columns found; the guard is not seeing the app.");
        Assert.True(offenders.Count == 0,
            "These grid columns show an enum or boolean value untranslated (give them a Template writing "
            + "L.Localize(value), the filter's own translator, or a badge carrying it):\n  "
            + string.Join("\n  ", offenders));
    }

    private static bool WritesRawValue(Match template, string property)
    {
        var context = Regex.Match(template.Groups["attributes"].Value, @"Context=""(?<name>\w+)""") is { Success: true } named
            ? named.Groups["name"].Value
            : "context";
        var raw = new Regex(@"@\(?" + Regex.Escape(context) + @"\." + Regex.Escape(property) + @"(?:\.ToString\(\))?\)?\s*<");
        return raw.IsMatch(template.Groups["body"].Value);
    }

    private static Type? PropertyType(string itemName, string path)
    {
        if (!TypesByName.TryGetValue(itemName, out var candidates) || candidates.Length != 1) return null;
        var type = candidates[0];
        foreach (var segment in path.Split('.'))
        {
            var property = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null) return null;
            type = property.PropertyType;
        }
        return type;
    }

    /// <summary>Each column's opening tag (ended at the first '&gt;' outside a quoted value, so a lambda's
    /// arrow does not cut it) and, when it is not self-closing, its body up to its closing tag.</summary>
    private static IEnumerable<(string OpeningTag, string Body, int Line)> Columns(string text)
    {
        foreach (Match open in ColumnOpen.Matches(text))
        {
            var index = open.Index;
            var inQuote = false;
            var end = -1;
            for (var i = index; i < text.Length; i++)
            {
                if (text[i] == '"') inQuote = !inQuote;
                else if (text[i] == '>' && !inQuote)
                {
                    end = i;
                    break;
                }
            }
            if (end < 0) continue;
            var opening = text[index..(end + 1)];
            var body = string.Empty;
            if (!opening.EndsWith("/>", StringComparison.Ordinal))
            {
                var close = text.IndexOf("</OmniDataGridColumn>", end, StringComparison.Ordinal);
                if (close > end) body = text[(end + 1)..close];
            }
            yield return (opening, body, text.AsSpan(0, index).Count('\n') + 1);
        }
    }
}
