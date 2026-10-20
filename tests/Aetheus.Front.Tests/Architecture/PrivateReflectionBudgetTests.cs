// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Prevents page tests from bypassing component invariants and keeps the remaining private-reflection
/// debt on a monotonic budget. When a test is migrated to bUnit or a public collaborator, lower the
/// corresponding ceiling; increases are not permitted.
/// </summary>
public sealed class PrivateReflectionBudgetTests
{
    private static readonly IReadOnlyDictionary<string, int> MaximumOccurrences =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // A360-10, 2026-08-21: tightened to the measured values, with no slack left. The audit
            // observed this budget falling by about eleven a month, which is a ratchet loose enough to
            // let a new test add debt as fast as an old one pays it. Every number here is now exactly
            // what the tree contains, so any addition fails immediately rather than being absorbed.
            // RFL-1, 2026-10-13: the AddAgent, DashboardEdit, PipelineEdit and ServerDockerSection suites are
            // driven through markup, child-component parameters and the faked services; the four numbers are
            // again exactly what the tree contains.
            // C3 (RFL-1), 2026-10-15: the dead AddAgent.CopyTokenAsync, PipelineEdit.OnPipelineRunStarted and
            // DockerContainersTab.BrowseParentAsync are deleted with the tests that reached them by reflection,
            // and the browse-panel test no longer forces a sub-directory path.
            ["BindingFlags.NonPublic"] = 607,
            [".GetMethod("] = 786,
            [".GetField("] = 1021,
            [".GetProperty("] = 277
        };

    [Fact]
    public void PageTests_DoNotBypassConstructorsOrIncreasePrivateReflectionDebt()
    {
        var pagesDirectory = Path.Combine(FindRepoRoot(), "tests", "Aetheus.Front.Tests", "Pages");
        var files = RepositoryScan.Enumerate(pagesDirectory, "*.cs").ToList();
        Assert.True(files.Count > 100, "The page-test source scan is unexpectedly small.");

        var sources = files.Select(File.ReadAllText).ToList();
        var uninitializedObjectUsers = files
            .Where((_, index) => sources[index].Contains("GetUninitializedObject", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(pagesDirectory, file))
            .ToList();
        Assert.True(uninitializedObjectUsers.Count == 0,
            "Page tests must use normal construction, bUnit, or a public collaborator; "
            + "GetUninitializedObject bypasses field initializers and constructor invariants:\n  "
            + string.Join("\n  ", uninitializedObjectUsers));

        var combinedSource = string.Join('\n', sources);
        var violations = MaximumOccurrences
            .Select(budget => new
            {
                budget.Key,
                budget.Value,
                Actual = CountOccurrences(combinedSource, budget.Key)
            })
            .Where(result => result.Actual > result.Value)
            .Select(result => $"{result.Key}: {result.Actual} > budget {result.Value}")
            .ToList();

        Assert.True(violations.Count == 0,
            "Private-reflection debt increased. Migrate the new test to bUnit/public behavior; "
            + "never raise the budget:\n  " + string.Join("\n  ", violations));
    }

    private static int CountOccurrences(string source, string token)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(token, index, StringComparison.Ordinal)) >= 0; index += token.Length)
            count++;
        return count;
    }

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
