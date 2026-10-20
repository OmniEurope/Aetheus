// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Using directives follow editorconfig and Roslyn ordering: System first, then
/// case-insensitive segment ordering. Static and alias directives belong to separate
/// groups.</summary>
public sealed partial class UsingDirectiveOrderAuditTests
{
    [GeneratedRegex(@"^using\s+([A-Za-z_][\w.]*)\s*;\s*$")]
    private static partial Regex NamespaceUsing();

    [Fact]
    public void NamespaceUsings_AreSystemFirstThenAlphabetical()
    {
        var files = new[] { "src", "tests" }
            .SelectMany(folder => RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, folder), "*.cs"))
            .ToList();

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var names = LeadingNamespaceUsings(File.ReadLines(file));
            for (var index = 1; index < names.Count; index++)
            {
                if (Compare(names[index - 1], names[index]) > 0)
                {
                    offenders.Add($"{Path.GetRelativePath(RepositoryScan.Root, file)}: '{names[index]}' before '{names[index - 1]}'");
                    break;
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Namespace using directives out of order (dotnet format IMPORTS). Fix with "
            + "`dotnet format <project> --include <file>`:\n" + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData("System.IO", "Aetheus.Tests.Shared")]
    [InlineData("System", "System.Diagnostics")]
    [InlineData("Aetheus.Back", "Aetheus.Tests.Shared")]
    [InlineData("Microsoft.Extensions.Options", "NSubstitute")]
    public void Compare_OrdersAsDotnetFormat(string first, string second)
    {
        Assert.True(Compare(first, second) < 0);
        Assert.True(Compare(second, first) > 0);
    }

    private static List<string> LeadingNamespaceUsings(IEnumerable<string> lines)
    {
        var names = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("using ", StringComparison.Ordinal) && line.EndsWith(';'))
            {
                var match = NamespaceUsing().Match(line);
                if (match.Success) names.Add(match.Groups[1].Value);
                continue;
            }

            break;
        }

        return names;
    }

    private static int Compare(string left, string right)
    {
        var leftSystem = IsSystem(left);
        if (leftSystem != IsSystem(right)) return leftSystem ? -1 : 1;

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var order = string.Compare(leftParts[index], rightParts[index], StringComparison.OrdinalIgnoreCase);
            if (order != 0) return order;
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static bool IsSystem(string name) =>
        name == "System" || name.StartsWith("System.", StringComparison.Ordinal);
}
