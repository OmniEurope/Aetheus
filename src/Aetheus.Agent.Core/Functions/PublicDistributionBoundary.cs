// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// <c>deploy/public-distribution/boundary.json</c> as the exporter reads it: what is listed at the top
/// level, what is left out, which paths are renamed (in file order, as <c>Object.entries</c> yields
/// them), the test methods and hook tasks dropped per file, the literals that must never leave and
/// what the verification ignores on both sides.
/// </summary>
internal sealed record PublicDistributionBoundary(
    IReadOnlyList<string> TopLevel,
    IReadOnlyList<string> Exclude,
    IReadOnlyList<KeyValuePair<string, string>> Rename,
    IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> DropTests,
    IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> DropTasks,
    IReadOnlyList<string> Forbidden,
    IReadOnlyList<string> CompareIgnore)
{
    /// <summary>The boundary of a commit, or why it cannot be used. Text that is not JSON is
    /// <see cref="BoundaryReading.IsUnreadable"/> (the script's <c>no boundary.json</c>); JSON of the wrong
    /// shape carries a <see cref="BoundaryReading.Problem"/> that stops the export of that commit, where
    /// the script stopped on a <c>TypeError</c>.</summary>
    public static BoundaryReading Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!PublicDistributionJson.TryParse(json, out var parsed))
            return BoundaryReading.Unreadable;
        if (parsed is not JsObject root)
            return BoundaryReading.Invalid("boundary.json is not an object");

        var problems = new List<string>();
        var topLevel = PublicDistributionJson.Strings(root, "topLevel", required: true, problems);
        var exclude = PublicDistributionJson.Strings(root, "exclude", required: true, problems);
        var forbidden = PublicDistributionJson.Strings(root, "forbidden", required: true, problems);
        var compareIgnore = PublicDistributionJson.Strings(root, "compareIgnore", required: false, problems);
        var rename = PublicDistributionJson.StringMap(root, "rename", required: true, problems);
        var dropTests = PublicDistributionJson.StringListMap(root, "dropTests", problems);
        var dropTasks = PublicDistributionJson.StringListMap(root, "dropTasks", problems);
        return problems.Count > 0
            ? BoundaryReading.Invalid(string.Join("; ", problems))
            : new BoundaryReading(new PublicDistributionBoundary(topLevel, exclude, rename, dropTests, dropTasks, forbidden, compareIgnore), null);
    }

    /// <summary>The <c>compareIgnore</c> list of the verifier's own checkout, <c>?? []</c> when absent.</summary>
    public static IReadOnlyList<string> ReadCompareIgnore(string json)
    {
        var reading = Read(json);
        if (reading.IsUnreadable)
            throw new PublicDistributionException("deploy/public-distribution/boundary.json of the workspace is not valid JSON.");
        if (reading.Boundary is null)
            throw new PublicDistributionException($"deploy/public-distribution/boundary.json of the workspace is unusable: {reading.Problem}.");
        return reading.Boundary.CompareIgnore;
    }
}

/// <summary>What <see cref="PublicDistributionBoundary.Read"/> found: a boundary, a problem with its shape,
/// or text that is not JSON at all.</summary>
internal sealed record BoundaryReading(PublicDistributionBoundary? Boundary, string? Problem)
{
    public static BoundaryReading Unreadable { get; } = new(null, null);

    public bool IsUnreadable => Boundary is null && Problem is null;

    public static BoundaryReading Invalid(string problem) => new(null, problem);
}

/// <summary>A failure of the input of the verification (the public checkout, Git, the workspace boundary):
/// the step fails with its message.</summary>
internal sealed class PublicDistributionException(string message) : Exception(message);
