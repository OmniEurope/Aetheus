// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Analytics bootstrap lists must cover static route segments except deliberately
/// unmeasured sign-in routes. Unknown segments otherwise disappear or become generic
/// values.</summary>
public sealed partial class AnalyticsRouteSegmentCoverageTests
{
    private static readonly HashSet<string> NotMeasuredTopLevel = new(StringComparer.Ordinal) { "login" };

    [Fact]
    public void EveryStaticSegmentOfAPageRoute_IsKnownToTheAnalyticsBootstrap()
    {
        var bootstrap = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "js", "aetheus-analytics-bootstrap.js"));
        var topLevel = SetOf(bootstrap, "topLevelSegments");
        var nested = SetOf(bootstrap, "staticNestedSegments");
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front"), "*.razor"))
        {
            foreach (Match route in PageDirective().Matches(File.ReadAllText(file)))
            {
                var segments = route.Groups["route"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < segments.Length; index++)
                {
                    var segment = segments[index];
                    if (segment.StartsWith('{')) continue;
                    if (index == 0 && NotMeasuredTopLevel.Contains(segment)) break;
                    var known = index == 0 ? topLevel : nested;
                    if (!known.Contains(segment))
                        missing.Add($"{(index == 0 ? "top-level" : "nested")} '{segment}' of {route.Groups["route"].Value}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "Add these segments to aetheus-analytics-bootstrap.js, or the pages are not counted:\n  "
            + string.Join("\n  ", missing));
    }

    private static HashSet<string> SetOf(string script, string name)
    {
        var declaration = Regex.Match(script, $@"const {name} = new Set\(\[(?<items>.*?)\]\);", RegexOptions.Singleline);
        Assert.True(declaration.Success, $"aetheus-analytics-bootstrap.js no longer declares {name}.");
        var items = QuotedItem().Matches(declaration.Groups["items"].Value).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(items);
        return items;
    }

    [GeneratedRegex("@page \"(?<route>/[^\"]*)\"")]
    private static partial Regex PageDirective();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedItem();
}
