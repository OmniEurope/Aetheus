// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Button labels use concise verbs because page titles supply context.</summary>
public sealed class ButtonLabelLengthAuditTests
{
    private const int MaxButtonLabelLength = 18;

    /// <summary>A whole button. Its visible label is a child, not a parameter, so the block has to be
    /// read as a unit: a title or an aria-label inside the opening tag is not what the user reads.</summary>
    private const string OmniButtonBlock = """<OmniButton\b(?:(?!</OmniButton>).)*?</OmniButton>""";

    /// <summary>The label the user actually reads on the button.</summary>
    private const string VisibleLabel = """<span[^>]*>\s*@L\["(?<key>\w+)"\]\s*</span>""";

    /// <summary>
    /// Labels that need their words. Each is a sentence the user acts on rather than a verb applied
    /// to the page they are already on, so shortening it would remove meaning, not context.
    /// </summary>
    private static readonly HashSet<string> JustifiedExceptions = new(StringComparer.Ordinal)
    {
        "ResumeCheckpoints", "RerunSameCommit",
        "InsufficientPermissions", "UpdateAllAgents",
        "BackToServers", "CreateVariableLibrary", "CreateGitRepository", "CreateEnvironment",
        "CreatePipeline", "AttachFirstServer",
        // Questions and qualified variants: the extra words ARE the meaning, not the page context.
        "WhyOffline", "ApplyOnProposedBranch", "PipelineRun",
        // Explicit security-settings wording.
        "ChangePassword",
        // The public-demo button names the form it fills because it sits outside that form.
        "PublicDemoFillForm"
    };

    [Fact]
    public void French_Button_Labels_Stay_Short()
    {
        var french = ReadResources("AppStrings.fr-FR.resx");
        var offenders = ButtonLabelKeys()
            .Where(key => !JustifiedExceptions.Contains(key))
            .Where(key => french.TryGetValue(key, out var label) && label.Length > MaxButtonLabelLength)
            .Select(key => $"{key} = \"{french[key]}\" ({french[key].Length})")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These button labels repeat the page context (over {MaxButtonLabelLength} chars) and are not "
            + $"in the justified list: {string.Join("; ", offenders)}");
    }

    /// <summary>Every resource key rendered as the visible label of a button.</summary>
    private static IEnumerable<string> ButtonLabelKeys()
    {
        var files = RepositoryScan.Enumerate(
            Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front"), "*.razor");

        var keys = files
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), OmniButtonBlock, RegexOptions.Singleline))
            .SelectMany(button => Regex.Matches(button.Value, VisibleLabel, RegexOptions.Singleline))
            .Select(match => match.Groups["key"].Value)
            .ToList();

        Assert.True(keys.Count >= 40, $"Only {keys.Count} button labels found; the scan is not seeing the app.");
        return keys;
    }

    private static Dictionary<string, string> ReadResources(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "Resources", fileName));

        return Regex.Matches(text, """<data name="(?<key>\w+)"[^>]*>\s*<value>(?<value>[^<]*)</value>""")
            .ToDictionary(match => match.Groups["key"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);
    }
}
