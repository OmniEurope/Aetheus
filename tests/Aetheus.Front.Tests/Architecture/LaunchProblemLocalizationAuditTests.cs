// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// R-113: a refused pipeline launch reaches the front as problem codes, and the front says each one
/// through the resource <c>LaunchProblem_{code}</c>. A code the back emits without both resources is
/// shown in English to a French user, which is the defect this guards. The codes are read from the
/// back's source: the constants of <c>PipelineProblemCodes</c> and every place that uses one.
/// </summary>
public sealed class LaunchProblemLocalizationAuditTests
{
    /// <summary>The back emitted 44 codes when this guard was written; far fewer means the scan broke.</summary>
    private const int MinimumCodes = 40;

    /// <summary>U+2014, written by its value so this file holds none.</summary>
    private const char EmDash = (char)0x2014;

    private static readonly Regex CodeConstant = new(
        @"public\s+const\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""]+)""\s*;", RegexOptions.CultureInvariant);

    private static readonly Regex CodeUse = new(@"\bPipelineProblemCodes\.(?<name>\w+)\b", RegexOptions.CultureInvariant);

    private static readonly Regex LiteralCode = new(
        @"(?:\bPipelineProblem\.Of|\bnew\s+PipelineProblem|\bnew\s+Reason)\s*\(\s*(?:\$|@)*""", RegexOptions.CultureInvariant);

    private static readonly Regex Placeholder = new(@"\{(?<name>[^{}]+)\}", RegexOptions.CultureInvariant);

    private static string BackRoot => Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back");

    private static string CodesFile => Path.Combine(BackRoot, "Components", "Pipelines", "PipelineProblemCodes.cs");

    [Fact]
    public void EveryEmittedLaunchProblemCode_HasAnEnglishAndAFrenchResource()
    {
        var english = ResourceValues("AppStrings.resx");
        var french = ResourceValues("AppStrings.fr-FR.resx");
        var codes = EmittedCodes();

        Assert.True(codes.Count >= MinimumCodes,
            $"Only {codes.Count} launch problem codes were found in the back: the scan is broken.");
        var missing = codes
            .Select(code => "LaunchProblem_" + code)
            .Append("LaunchProblemInChain")
            .SelectMany(key => new[] { (Key: key, File: "AppStrings.resx", Values: english), (Key: key, File: "AppStrings.fr-FR.resx", Values: french) })
            .Where(entry => !entry.Values.ContainsKey(entry.Key))
            .Select(entry => $"{entry.File}: {entry.Key}")
            .ToList();
        Assert.True(missing.Count == 0,
            "A launch problem code the back emits has no resource, so the refusal is shown in English:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryLaunchProblemResource_NamesTheSamePlaceholdersInBothLanguages_WithoutAnEmDash()
    {
        var english = ResourceValues("AppStrings.resx");
        var french = ResourceValues("AppStrings.fr-FR.resx");
        var keys = english.Keys.Where(key => key.StartsWith("LaunchProblem", StringComparison.Ordinal)).ToList();
        Assert.True(keys.Count >= MinimumCodes, $"Only {keys.Count} LaunchProblem resources were found: the scan is broken.");

        var violations = new List<string>();
        foreach (var key in keys)
        {
            if (!french.TryGetValue(key, out var frenchValue)) continue; // reported by the test above
            var englishNames = PlaceholderNames(english[key]);
            var frenchNames = PlaceholderNames(frenchValue);
            if (!englishNames.SetEquals(frenchNames))
                violations.Add($"{key}: English {{{string.Join("}, {", englishNames.Order())}}} / French {{{string.Join("}, {", frenchNames.Order())}}}");
            // Named placeholders only: the parameters are a dictionary, never a position.
            if (englishNames.Concat(frenchNames).Any(name => name.All(char.IsDigit)))
                violations.Add($"{key}: positional placeholder");
            if (english[key].Contains(EmDash) || frenchValue.Contains(EmDash))
                violations.Add($"{key}: em dash");
        }

        Assert.True(violations.Count == 0,
            "A LaunchProblem resource differs between languages:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EveryLaunchProblem_TakesItsCodeFromPipelineProblemCodes()
    {
        var problems = 0;
        var violations = new List<string>();
        foreach (var file in RepositoryScan.Enumerate(BackRoot, "*.cs"))
        {
            var source = File.ReadAllText(file);
            var masked = SourceMasker.MaskCommentsAndStrings(source);
            foreach (Match match in LiteralCode.Matches(source))
            {
                if (masked[match.Index] != source[match.Index]) continue;
                problems++;
                violations.Add($"{Path.GetRelativePath(BackRoot, file)}:{source.AsSpan(0, match.Index).Count('\n') + 1}");
            }
            problems += CodeUse.Matches(masked).Count;
        }

        Assert.True(problems >= MinimumCodes, $"Only {problems} launch problems were found in the back: the scan is broken.");
        Assert.True(violations.Count == 0,
            "A launch problem code is written as a literal, out of the guard's sight: use a PipelineProblemCodes constant:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Scanner_ReadsConstantsAndUses_AndIgnoresComments()
    {
        const string codes = """
            public const string Alpha = "Alpha";
            // public const string Commented = "Commented";
            public const string Beta = "Beta";
            """;
        const string use = """
            var a = PipelineProblem.Of(PipelineProblemCodes.Alpha, "x");
            // PipelineProblemCodes.Beta
            var b = PipelineProblem.Of("Literal", "x");
            """;

        Assert.Equal(["Alpha", "Beta"], Constants(codes).Keys.Order());
        Assert.Equal(["Alpha"], CodeUse.Matches(SourceMasker.MaskCommentsAndStrings(use)).Select(match => match.Groups["name"].Value));
        Assert.Single(LiteralCode.Matches(use));
    }

    /// <summary>The value of every code constant some back file uses.</summary>
    private static HashSet<string> EmittedCodes()
    {
        var constants = Constants(File.ReadAllText(CodesFile));
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in RepositoryScan.Enumerate(BackRoot, "*.cs"))
        {
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(CodesFile), StringComparison.OrdinalIgnoreCase)) continue;
            var masked = SourceMasker.MaskCommentsAndStrings(File.ReadAllText(file));
            foreach (Match match in CodeUse.Matches(masked))
            {
                var name = match.Groups["name"].Value;
                Assert.True(constants.TryGetValue(name, out var value), $"{Path.GetRelativePath(BackRoot, file)} uses an unknown code {name}.");
                used.Add(value!);
            }
        }
        return used;
    }

    private static Dictionary<string, string> Constants(string source)
    {
        var masked = SourceMasker.MaskCommentsAndStrings(source);
        return CodeConstant.Matches(source)
            .Where(match => masked[match.Index] == source[match.Index])
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ResourceValues(string fileName) =>
        XDocument.Load(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Resources", fileName))
            .Root!.Elements("data")
            .GroupBy(data => (string)data.Attribute("name")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (string?)group.Last().Element("value") ?? string.Empty, StringComparer.Ordinal);

    private static HashSet<string> PlaceholderNames(string value) =>
        Placeholder.Matches(value).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
}
