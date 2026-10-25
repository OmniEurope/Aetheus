// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The distribution of one revision, in memory: the second half of <c>buildDistribution</c> of
/// <c>scripts/export-public-distribution.mjs</c> (the planning and the reading of the tree are
/// <see cref="PublicDistributionPlan"/> and <see cref="PublicDistributionSource"/>), and
/// <c>compareTrees</c>. Whatever the script threw with the reason the export would be refused (a stale
/// <c>dropTests</c> name, a hook task, a forbidden literal) is a <see cref="PublicDistribution.Refusal"/>.
/// </summary>
internal static class PublicDistributionBuilder
{
    private const string HuskyRunner = ".husky/task-runner.json";
    private const int TextProbeLength = 8000;

    /// <summary>Applies the boundary to the files of the plan: references to renamed paths rewritten, the
    /// dropped tests and hook tasks removed, the forbidden literals searched.</summary>
    public static PublicDistribution Assemble(
        IReadOnlyDictionary<string, string> copies, IReadOnlyDictionary<string, byte[]> tree, PublicDistributionBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(boundary);
        var contents = Rewrite(copies, tree, boundary);

        // Tests that read a path left out of the distribution cannot pass there: they are removed, never
        // weakened (manifest rule 1). A listed file or method that no longer exists stops the export.
        var staleDrops = DropTests(contents, boundary);
        if (staleDrops.Count > 0)
            return PublicDistribution.Refused($"boundary.json dropTests names tests the source no longer has:\n  {string.Join("\n  ", staleDrops)}");

        // Hook tasks that run a file left out of the distribution are removed by name (dropTasks); a stale
        // name, or a remaining task that still names an absent file, stops the export.
        var taskProblems = DropHookTasks(contents, boundary);
        if (taskProblems.Count > 0)
            return PublicDistribution.Refused($"boundary.json dropTasks does not match the hooks:\n  {string.Join("\n  ", taskProblems)}");

        var hits = PublicDistributionEdits.FindForbidden(
            contents.Where(file => IsText(file.Value)).Select(file => KeyValuePair.Create(file.Key, Encoding.Latin1.GetString(file.Value))),
            boundary.Forbidden);
        return hits.Count > 0 ? PublicDistribution.Refused(PublicDistributionEdits.Describe(hits)) : new PublicDistribution(contents, null);
    }

    /// <summary>Where a published tree and a distribution disagree, the ignored paths left out on both sides:
    /// <c>missing</c> (in the distribution only), <c>unexpected</c> (published only) or <c>changed</c>.
    /// Empty when they are equal.</summary>
    public static IReadOnlyList<string> Compare(
        IReadOnlyDictionary<string, byte[]> distribution, IReadOnlyDictionary<string, byte[]> published, IEnumerable<string> ignore)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        ArgumentNullException.ThrowIfNull(published);
        var ignored = new PublicDistributionGlobs(ignore);
        var differences = new List<string>();
        foreach (var path in Kept(distribution, ignored))
        {
            if (!published.TryGetValue(path, out var other))
                differences.Add($"missing: {path}");
            else if (!other.AsSpan().SequenceEqual(distribution[path]))
                differences.Add($"changed: {path}");
        }
        differences.AddRange(Kept(published, ignored).Where(path => !distribution.ContainsKey(path)).Select(path => $"unexpected: {path}"));
        return differences;
    }

    /// <summary><c>!buffer.subarray(0, 8000).includes(0)</c>: text is a file with no NUL byte at its start.</summary>
    public static bool IsText(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return !content.AsSpan(0, Math.Min(TextProbeLength, content.Length)).Contains((byte)0);
    }

    private static IEnumerable<string> Kept(IReadOnlyDictionary<string, byte[]> tree, PublicDistributionGlobs ignored) =>
        tree.Keys.Where(path => !ignored.IsMatch(path)).Order(StringComparer.Ordinal);

    /// <summary>The files by destination path; latin1 keeps every byte as it is (BOM, encoding), so only
    /// the ASCII names are rewritten, and the overlay is taken as written.</summary>
    private static Dictionary<string, byte[]> Rewrite(
        IReadOnlyDictionary<string, string> copies, IReadOnlyDictionary<string, byte[]> tree, PublicDistributionBoundary boundary)
    {
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (target, from) in copies)
        {
            var buffer = tree[from];
            if (IsText(buffer) && !from.StartsWith(PublicDistributionPlan.Overlay, StringComparison.Ordinal))
            {
                var text = Encoding.Latin1.GetString(buffer);
                var rewritten = PublicDistributionEdits.RewriteReferences(text, boundary.Rename);
                if (!string.Equals(rewritten, text, StringComparison.Ordinal))
                    buffer = Encoding.Latin1.GetBytes(rewritten);
            }
            contents[target] = buffer;
        }
        return contents;
    }

    private static List<string> DropTests(Dictionary<string, byte[]> contents, PublicDistributionBoundary boundary)
    {
        var stale = new List<string>();
        foreach (var (path, names) in boundary.DropTests)
        {
            if (!contents.TryGetValue(path, out var buffer))
            {
                stale.Add($"{path} (file)");
                continue;
            }
            var (text, missing) = PublicDistributionEdits.DropTestMethods(Encoding.Latin1.GetString(buffer), names);
            stale.AddRange(missing.Select(name => $"{path}: {name}"));
            contents[path] = Encoding.Latin1.GetBytes(text);
        }
        return stale;
    }

    /// <summary>The Husky runner is always checked, listed or not, so a new task on an excluded script is
    /// caught.</summary>
    private static List<string> DropHookTasks(Dictionary<string, byte[]> contents, PublicDistributionBoundary boundary)
    {
        var hookFiles = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        if (contents.ContainsKey(HuskyRunner))
            hookFiles.Add(KeyValuePair.Create(HuskyRunner, (IReadOnlyList<string>)[]));
        foreach (var entry in boundary.DropTasks)
        {
            var at = hookFiles.FindIndex(file => file.Key == entry.Key);
            if (at >= 0)
                hookFiles[at] = entry;
            else
                hookFiles.Add(entry);
        }

        var problems = new List<string>();
        foreach (var (path, names) in hookFiles)
        {
            if (!contents.TryGetValue(path, out var buffer))
            {
                problems.Add($"{path} (file)");
                continue;
            }
            var edit = PublicDistributionEdits.DropHookTasks(Encoding.UTF8.GetString(buffer), names, contents.Keys.ToHashSet(StringComparer.Ordinal));
            if (edit is null)
            {
                problems.Add($"{path}: not a JSON task runner");
                continue;
            }
            problems.AddRange(edit.Missing.Select(name => $"{path}: no task '{name}'"));
            problems.AddRange(edit.Dangling.Select(entry => $"{path}: task {entry} is not in the distribution"));
            if (names.Count > 0)
                contents[path] = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(edit.Text);
        }
        return problems;
    }
}

/// <summary>A distribution (path to bytes), or the reason its export would be refused.</summary>
internal sealed record PublicDistribution(IReadOnlyDictionary<string, byte[]> Contents, string? Refusal)
{
    public static PublicDistribution Refused(string reason) => new(new Dictionary<string, byte[]>(), reason);
}
