// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// R-113 (rule 6, everything the user reads is French, no raw technical value). <see cref="EnumColumnRenderAuditTests"/>
/// covers grid columns only; an enum written straight into free markup (a badge, a fact tile, a tooltip,
/// a grid template cell) shipped raw ("Information", "GreaterThan", "ServerCount") next to the page that
/// localized the very same enum.
///
/// The guard resolves the type of the receiver of every <c>@x.Prop</c> markup expression: a
/// <c>@foreach (var x in coll)</c> loop variable, a <c>Context="x"</c> of a templated component (typed by the
/// nearest <c>TItem</c>), or a field/property declared in the page or its code-behind. When the last property
/// of the chain is an enum, the expression must go through the localizer (<c>L.Localize(x.Prop)</c>) or a helper;
/// writing it as is fails. A variable name bound to several types in one file is checked against each of them
/// and flagged only when the property is an enum on all those that declare it.
///
/// Allowed: a value used as a CSS class suffix (inside a <c>class=""</c> attribute, never shown), and the
/// enums whose members ARE the universal display text (the A to F grade letters).
/// </summary>
public sealed class EnumMarkupLocalizationAuditTests
{
    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled);

    private const string Chain = @"[A-Za-z_][A-Za-z0-9_]*(?:\??\.[A-Za-z_][A-Za-z0-9_]*)*";

    // @a.b.Prop[.ToString()] or @(a.b.Prop[.ToString()]), followed by nothing that turns it into something else.
    private static readonly Regex Expression = new(
        @"@(?:(?<chain>" + Chain + @")(?![A-Za-z0-9_(\[])(?:\.ToString\(\))?(?!\.)|\(\s*(?<chain>" + Chain + @")(?:\??\.ToString\(\))?\s*(?:\)|\?\?))",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // The attribute (name) whose quoted value the expression is inside, on the same line.
    private static readonly Regex OpenAttribute = new(@"(?<![\w-])(?<name>[A-Za-z_:@][\w:.@-]*)\s*=\s*""[^""]*$", RegexOptions.Compiled);

    /// <summary>Attributes whose value is read by the user; any other attribute is a component parameter,
    /// a CSS class, a data attribute or a binding, which the component itself localizes or never shows.</summary>
    private static readonly HashSet<string> DisplayAttributes = new(StringComparer.OrdinalIgnoreCase)
        { "title", "aria-label", "placeholder", "alt", "label", "text", "tooltip", "description", "caption", "subtitle", "content" };

    /// <summary>True for element text and for the display attributes; false inside a class, data or parameter attribute.</summary>
    private static bool IsShownToTheUser(string lineBeforeExpression)
    {
        var attribute = OpenAttribute.Match(lineBeforeExpression);
        return !attribute.Success || DisplayAttributes.Contains(attribute.Groups["name"].Value);
    }
    private static readonly Regex ForEach = new(@"\bforeach\s*\(\s*(?:var|[\w<>\.]+)\s+(?<var>\w+)\s+in\s+(?<source>" + Chain + @")(?=\s*\)|\.(?:Where|OrderBy|OrderByDescending|ThenBy|ThenByDescending|Take|Skip|ToList|ToArray|Reverse)\b)", RegexOptions.Compiled);
    // for (...) { var item = coll[index]; ... }
    private static readonly Regex IndexedItem = new(@"\bvar\s+(?<var>\w+)\s*=\s*(?<source>" + Chain + @")\[[^\]]+\]\s*;", RegexOptions.Compiled);
    private static readonly Regex ContextAttribute = new(@"\bContext=""(?<var>\w+)""", RegexOptions.Compiled);
    private static readonly Regex ItemAttribute = new(@"\bTItem=""(?<type>\w+)""", RegexOptions.Compiled);
    private static readonly Regex Declaration = new(
        @"^[ \t]*(?:\[[^\]\r\n]*\][ \t]*)*(?:public|private|protected|internal)[ \t]+(?:static[ \t]+|readonly[ \t]+|required[ \t]+)*(?<type>[\w\.]+(?:<[^;=(){}]*>)?\??(?:\[\])?)[ \t]+(?<name>\w+)[ \t]*(?:;|=|\{|=>)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Enums whose member names are the text shown in every language.</summary>
    private static readonly HashSet<string> LanguageNeutralEnums = new(StringComparer.Ordinal) { "AnalysisGrade" };

    private static readonly Lazy<IReadOnlyDictionary<string, Type>> TypesByName = new(() =>
        new[]
            {
                typeof(Aetheus.Front.Components.Shared.AetheusDataGrid<>).Assembly,
                typeof(Aetheus.Shared.Components.Pipelines.PipelineStatus).Assembly
            }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsGenericTypeDefinition)
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    [Fact]
    public void An_Enum_Property_Is_Never_Written_Raw_In_Markup()
    {
        var root = RepositoryScan.Root;
        var offenders = new List<string>();
        var checkedExpressions = 0;
        var enumExpressions = 0;

        foreach (var file in RepositoryScan.Enumerate(Path.Combine(root, "src", "Aetheus.Front"), "*.razor"))
        {
            var text = RazorComment.Replace(File.ReadAllText(file), match => Regex.Replace(match.Value, @"[^\n]", " "));
            var code = text + "\n" + (File.Exists(file + ".cs") ? File.ReadAllText(file + ".cs") : string.Empty);
            var variables = Variables(text, code);

            foreach (Match match in Expression.Matches(text))
            {
                var segments = match.Groups["chain"].Value.Replace("?.", ".").Split('.');
                if (segments.Length < 2) continue;
                if (!variables.TryGetValue(segments[0], out var candidates)) continue;
                checkedExpressions++;

                var finals = candidates
                    .Select(type => Walk(type, segments[1..]))
                    .OfType<Type>()
                    .Select(type => Nullable.GetUnderlyingType(type) ?? type)
                    .ToList();
                if (finals.Count == 0 || !finals.All(type => type.IsEnum)) continue;
                enumExpressions++;
                if (finals.All(type => LanguageNeutralEnums.Contains(type.Name))) continue;

                var before = text[(text.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1)..match.Index];
                if (!IsShownToTheUser(before)) continue;
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                offenders.Add($"{Path.GetRelativePath(root, file)}:{line}: {match.Value} shows the raw {finals[0].Name} value");
            }
        }

        Assert.True(checkedExpressions >= 300, $"Only {checkedExpressions} typed markup expressions resolved; the guard is not seeing the app.");
        Assert.True(enumExpressions >= 10, $"Only {enumExpressions} enum expressions resolved; the guard is not seeing the app.");
        Assert.True(offenders.Count == 0,
            "These markup expressions write an enum value to the user without the localizer (use "
            + "L.Localize(value), or a helper returning a localized label or a symbol):\n  "
            + string.Join("\n  ", offenders.Distinct().OrderBy(o => o, StringComparer.Ordinal)));
    }

    [Fact]
    public void A_Grid_Column_Title_Is_Never_A_Plain_Word_Outside_The_Technical_Names()
    {
        // "Gate" was a literal Title on the analysis portfolio while the same word is a resx key: a literal
        // title never reaches the French file. Only the technical acronyms below may stay literal.
        var technical = new HashSet<string>(StringComparer.Ordinal) { "ID", "UID", "DKIM", "DNS" };
        var literal = new Regex(@"<OmniDataGridColumn\b[^>]*?\sTitle=""(?<title>[A-Za-z][^""@]*)""", RegexOptions.Compiled);
        var root = RepositoryScan.Root;
        var offenders = new List<string>();

        foreach (var file in RepositoryScan.Enumerate(Path.Combine(root, "src", "Aetheus.Front"), "*.razor"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in literal.Matches(text))
            {
                var title = match.Groups["title"].Value;
                if (technical.Contains(title)) continue;
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                offenders.Add($"{Path.GetRelativePath(root, file)}:{line}: Title=\"{title}\" is a literal, use L[\"Key\"]");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    [Fact]
    public void A_Dropdown_Option_Of_An_Enum_Is_Never_Labelled_With_The_Enum_Name()
    {
        // new OmniOption<GitProviderType>(t, t.ToString()) lists "AetheusGit" or "HttpsToken" in the dropdown.
        var raw = new Regex(@"new\s+OmniOption<(?<type>\w+)>\(\s*(?<v>\w+)\s*,\s*\k<v>\.ToString\(\)\s*\)", RegexOptions.Compiled);
        var root = RepositoryScan.Root;
        var offenders = new List<string>();

        foreach (var file in RepositoryScan.Enumerate(Path.Combine(root, "src", "Aetheus.Front"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in raw.Matches(text))
            {
                if (!TypesByName.Value.TryGetValue(match.Groups["type"].Value, out var type) || !type.IsEnum) continue;
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                offenders.Add($"{Path.GetRelativePath(root, file)}:{line}: {match.Value} (use L.Localize({match.Groups["v"].Value}))");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    /// <summary>Variable name to the types it can have in this file: declared members, loop variables, template contexts.</summary>
    private static Dictionary<string, List<Type>> Variables(string markup, string code)
    {
        var result = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
        void Add(string name, Type? type)
        {
            if (type is null) return;
            if (!result.TryGetValue(name, out var list)) result[name] = list = [];
            if (!list.Contains(type)) list.Add(type);
        }

        foreach (Match declaration in Declaration.Matches(code))
            Add(declaration.Groups["name"].Value, ParseType(declaration.Groups["type"].Value));

        foreach (Match context in ContextAttribute.Matches(markup))
        {
            var item = ItemAttribute.Matches(markup[..context.Index]).LastOrDefault();
            if (item is not null && TypesByName.Value.TryGetValue(item.Groups["type"].Value, out var type))
                Add(context.Groups["var"].Value, type);
        }

        foreach (Match loop in ForEach.Matches(markup).Concat(IndexedItem.Matches(markup)))
        {
            var segments = loop.Groups["source"].Value.Replace("?.", ".").Split('.');
            if (!result.TryGetValue(segments[0], out var sources)) continue;
            foreach (var source in sources.ToList())
                Add(loop.Groups["var"].Value, ElementType(Walk(source, segments[1..])));
        }
        return result;
    }

    private static Type? Walk(Type type, IEnumerable<string> segments)
    {
        Type? current = type;
        foreach (var segment in segments)
        {
            current = current is null ? null : (Nullable.GetUnderlyingType(current) ?? current);
            var property = current?.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null) return null;
            current = property.PropertyType;
        }
        return current;
    }

    private static Type? ElementType(Type? collection)
    {
        if (collection is null) return null;
        if (collection.IsArray) return collection.GetElementType();
        return collection.IsGenericType && collection.GetGenericArguments().Length == 1 && collection != typeof(string)
            ? collection.GetGenericArguments()[0]
            : null;
    }

    /// <summary>A declared type name ("TaskDto?", "List&lt;LogDto&gt;", "LogDto[]") to its type, for the shapes the markup iterates.</summary>
    private static Type? ParseType(string name)
    {
        name = name.TrimEnd('?');
        if (name.EndsWith("[]", StringComparison.Ordinal))
            return ParseType(name[..^2])?.MakeArrayType();
        var open = name.IndexOf('<');
        if (open < 0)
            return TypesByName.Value.TryGetValue(name[(name.LastIndexOf('.') + 1)..], out var simple) ? simple : null;
        var arguments = name[(open + 1)..^1];
        if (arguments.Contains(',') || arguments.Contains('<') && !arguments.EndsWith('>')) return null;
        return ParseType(arguments) is { } inner ? typeof(List<>).MakeGenericType(inner) : null;
    }
}
