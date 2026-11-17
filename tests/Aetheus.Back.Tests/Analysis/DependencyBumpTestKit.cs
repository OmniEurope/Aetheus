// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis.DependencyBump;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// What the dependency bump tests share: the manifests copied from this repository (and the Composer pair written by hand, as no
/// PHP or Composer is on the machine to produce one), and the checks every bump answer has to pass.
/// </summary>
internal static class DependencyBumpTestKit
{
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "dependency-bump", name + ".fixture"));

    public static DependencyBumpRequest Request(string ecosystem, string package, string current, string target, string source) =>
        new(ecosystem, package, current, target, source);

    public static DependencyBumpResult Plan(DependencyBumpRequest request, params (string Path, string Content)[] files) =>
        DependencyBumper.Plan(request, files.ToDictionary(file => file.Path, file => file.Content));

    /// <summary>A bump that edited files: the one edit, whose text is the original with exactly <paramref name="oldText"/> replaced once.</summary>
    public static DependencyBumpFileEdit SingleEdit(DependencyBumpResult result, string path, string original, string oldText, string newText)
    {
        Assert.Equal(DependencyBumpRefusal.None, result.Refusal);
        var edit = Assert.Single(result.Edits);
        Assert.Equal(path, edit.Path);
        Assert.Equal(1, CountOf(original, oldText));
        Assert.Equal(original.Replace(oldText, newText, StringComparison.Ordinal), edit.Content);
        return edit;
    }

    public static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>The 1-based line a text starts on, to check what a change says about where it is.</summary>
    public static int LineOf(string text, string part) => text[..text.IndexOf(part, StringComparison.Ordinal)].Count(c => c == '\n') + 1;

    /// <summary>A refusal never carries an edit or a lock file: nothing is half done.</summary>
    public static void AssertRefused(DependencyBumpResult result, DependencyBumpRefusal refusal)
    {
        Assert.Equal(DependencyBumpOutcome.Refused, result.Outcome);
        Assert.Equal(refusal, result.Refusal);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Empty(result.Edits);
        Assert.Empty(result.LockRefreshes);
    }
}
