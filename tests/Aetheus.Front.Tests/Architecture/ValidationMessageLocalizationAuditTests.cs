// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>ValidationResult messages use localized text, attribute-message keys exist in both
/// resources and OE validators receive the form localizer.</summary>
public sealed class ValidationMessageLocalizationAuditTests
{
    private static readonly Regex ValidationResultCall = new(@"\bnew\s+ValidationResult\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex StringLiteralStart = new(@"^(?:\$@|@\$|\$|@)?""", RegexOptions.CultureInvariant);

    private static readonly Regex ErrorMessageLiteral = new(@"\bErrorMessage\s*=\s*""(?<key>[^""]*)""", RegexOptions.CultureInvariant);

    private static readonly Regex ResourceKey = new(@"<data name=""(?<key>[^""]+)""", RegexOptions.CultureInvariant);

    private static readonly Regex AnnotationsValidatorTag = new(@"<(?<name>Omni)?DataAnnotationsValidator\b(?<attributes>[^>]*)>", RegexOptions.CultureInvariant);

    private static string FrontRoot => Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");

    [Fact]
    public void NoValidationResult_TakesAStringLiteralAsItsMessage()
    {
        var calls = 0;
        var violations = new List<string>();
        foreach (var file in RepositoryScan.Enumerate(FrontRoot, "*.cs"))
        {
            var source = File.ReadAllText(file);
            var found = LiteralMessages(source, out var fileCalls);
            calls += fileCalls;
            violations.AddRange(found.Select(hit => $"{Path.GetRelativePath(FrontRoot, file)}:{hit}"));
        }

        Assert.True(calls >= 20, $"Only {calls} 'new ValidationResult(' calls were found in the front: the scan is broken.");
        Assert.True(violations.Count == 0,
            "A ValidationResult message is shown as it is: read it through the localizer (new ValidationResult(L[\"Key\"], ...)):"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Scanner_FindsLiteralMessages_AndIgnoresCommentsAndLocalizedOnes()
    {
        const string source = """
            // yield return new ValidationResult("InComment", [nameof(Email)]);
            yield return new ValidationResult(L["MailEmailInvalid"], [nameof(Email)]);
            yield return new ValidationResult("MailEmailInvalid", [nameof(Email)]);
            yield return new ValidationResult(
                $"Raw {value}", [nameof(Email)]);
            var text = "new ValidationResult(\"InString\")";
            """;

        var hits = LiteralMessages(source, out var calls);

        Assert.Equal(3, calls);
        Assert.Equal(["3: \"MailEmailInvalid\"", "4: $\"Raw {value}\""], hits);
    }

    [Fact]
    public void EveryValidationAttributeErrorMessage_IsAResourceKeyInBothLanguages()
    {
        var english = ResourceKeys("AppStrings.resx");
        var french = ResourceKeys("AppStrings.fr-FR.resx");
        var messages = 0;
        var violations = new List<string>();
        foreach (var file in RepositoryScan.Enumerate(FrontRoot, "*.cs"))
        {
            var source = File.ReadAllText(file);
            var masked = SourceMasker.MaskCommentsAndStrings(source);
            foreach (Match match in ErrorMessageLiteral.Matches(source))
            {
                // The masker blanks comments and literals: a match whose first letter survives is code.
                if (masked[match.Index] != source[match.Index]) continue;
                messages++;
                var key = match.Groups["key"].Value;
                if (!english.Contains(key) || !french.Contains(key))
                    violations.Add($"{Path.GetRelativePath(FrontRoot, file)}:{LineAt(source, match.Index)}: \"{key}\"");
            }
        }

        Assert.True(messages >= 5, $"Only {messages} ErrorMessage literals were found in the front: the scan is broken.");
        Assert.True(violations.Count == 0,
            "An ErrorMessage that is not a key of both resource files is shown untranslated:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EveryAnnotationsValidator_IsTheOmniOne_WithTheFormsLocalizer()
    {
        var validators = 0;
        var violations = new List<string>();
        foreach (var file in RepositoryScan.Enumerate(FrontRoot, "*.razor"))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in AnnotationsValidatorTag.Matches(source))
            {
                validators++;
                if (!match.Groups["name"].Success || !match.Groups["attributes"].Value.Contains("Localizer=", StringComparison.Ordinal))
                    violations.Add($"{Path.GetRelativePath(FrontRoot, file)}:{LineAt(source, match.Index)}: {match.Value}");
            }
        }

        Assert.True(validators >= 20, $"Only {validators} annotations validators were found in the front: the scan is broken.");
        Assert.True(violations.Count == 0,
            "Without the localizer, OE shows an ErrorMessage key as it is: use <OmniDataAnnotationsValidator Localizer=\"@L\" />:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>The literal messages given to <c>new ValidationResult(</c>, as "line: literal start".</summary>
    private static List<string> LiteralMessages(string source, out int calls)
    {
        var masked = SourceMasker.MaskCommentsAndStrings(source);
        var hits = new List<string>();
        calls = 0;
        foreach (Match match in ValidationResultCall.Matches(masked))
        {
            calls++;
            var argument = source[(match.Index + match.Length)..].TrimStart();
            if (!StringLiteralStart.IsMatch(argument)) continue;
            var end = argument.IndexOfAny([',', ')', '\n']);
            hits.Add($"{LineAt(source, match.Index)}: {(end < 0 ? argument : argument[..end]).Trim()}");
        }

        return hits;
    }

    private static HashSet<string> ResourceKeys(string fileName) =>
        ResourceKey.Matches(File.ReadAllText(Path.Combine(FrontRoot, "Resources", fileName)))
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static int LineAt(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;
}
