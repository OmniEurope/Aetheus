// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>generate-package-sbom</c> function, port of <c>deploy/scripts/generate-package-sbom.mjs</c>:
/// writes the deterministic SPDX 2.3 JSON SBOM of a NuGet or npm package byte for byte as the script
/// does (<see cref="JsJson.StringifyIndented"/> plus a line feed). The package and its dependencies
/// (<see cref="PackageSbomDependencies"/>), the package itself excluded from them, become one entry per
/// SPDX id (the first place, the last value, as a <c>Map</c> keeps them), sorted with
/// <see cref="JsLocaleCompare"/>, each with its purl (<see cref="JsText.EncodeUriComponent"/>); the
/// creation time is <c>new Date(Number.parseInt(SOURCE_DATE_EPOCH ?? "0", 10) * 1000).toISOString()</c>
/// and the epoch itself is refused.
///
/// Parameters are the script's arguments, paths relative to the workspace. <c>source_date_epoch</c>,
/// when not empty, stands for the <c>SOURCE_DATE_EPOCH</c> variable the script read, which is otherwise
/// read as is (an empty variable is an invalid time, as for the script). The output directory is not
/// created, as the script did not create it.
///
/// Differences, deliberate: an empty required parameter is refused by the request validation with its
/// own message (the script printed its usage); a file that cannot be read or written, or input that is
/// not JSON, fails with the .NET message instead of Node's; a NuGet id outside ASCII is refused (its
/// JavaScript lowercase is not reproduced), as is an SPDX id outside ASCII to sort, which the
/// replacement of every character outside <c>[A-Za-z0-9.-]</c> rules out.
/// </summary>
public sealed partial class GeneratePackageSbomFunction : IPipelineFunction
{
    private const string EpochVariable = "SOURCE_DATE_EPOCH";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex("^[0-9a-fA-F]{40}([0-9a-fA-F]{24})?\\z")]
    private static partial Regex SourceCommit();

    [GeneratedRegex("[^A-Za-z0-9.-]")]
    private static partial Regex UnsafeSpdxCharacter();

    public string Name => PipelineFunctionCatalog.GeneratePackageSbom.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var ecosystem = context.Parameters["ecosystem"];
        var productName = context.Parameters["name"];
        var version = context.Parameters["version"];
        var sourceCommit = context.Parameters["source_commit"];
        if (ecosystem is not ("nuget" or "npm") || !SourceCommit().IsMatch(sourceCommit))
            throw new JsError("Usage: generate-package-sbom.mjs <nuget|npm> <input> <name> <version> <source-commit> <output>");

        var created = JsText.ToIsoString(JsText.ParseInt(EpochText(context)) * 1000);
        if (created == "1970-01-01T00:00:00.000Z")
            throw new JsError("SOURCE_DATE_EPOCH must contain the immutable source commit timestamp.");

        var input = JsJson.Parse(Utf8.GetString(await File.ReadAllBytesAsync(
            Resolve(context.Workspace, context.Parameters["input"]), cancellationToken).ConfigureAwait(false)));
        var dependencies = ecosystem == "nuget" ? PackageSbomDependencies.ReadNuGet(input) : PackageSbomDependencies.ReadNpm(input);
        var document = BuildDocument(ecosystem, productName, version, sourceCommit, created, dependencies);
        await File.WriteAllBytesAsync(
            Resolve(context.Workspace, context.Parameters["output"]),
            Utf8.GetBytes(JsJson.StringifyIndented(document) + "\n"),
            cancellationToken).ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary><c>process.env.SOURCE_DATE_EPOCH ?? "0"</c>, the parameter standing for the variable.</summary>
    private static string EpochText(PipelineFunctionContext context) =>
        context.Parameters.GetValueOrDefault("source_date_epoch") is { Length: > 0 } given
            ? given
            : context.RunVariables.GetValueOrDefault(EpochVariable) ?? Environment.GetEnvironmentVariable(EpochVariable) ?? "0";

    private static JsObject BuildDocument(
        string ecosystem, string productName, string version, string sourceCommit, string created, List<PackageSbomDependency> dependencies)
    {
        var rootId = SpdxId(productName, version);
        var packages = new List<JsObject> { PackageEntry(productName, version, rootId, ecosystem) };
        packages.AddRange(dependencies
            .Where(item => !(JsValue.StrictEquals(item.Name, productName) && JsValue.StrictEquals(item.Version, version)))
            .ToList()
            .Select(item => PackageEntry(item.Name, item.Version, SpdxId(item.Name, item.Version), ecosystem)));
        var unique = JsLocaleCompare.Sort(UniqueBySpdxId(packages), item => (string)item["SPDXID"]!);

        var relationships = new List<object?> { Relationship("SPDXRef-DOCUMENT", "DESCRIBES", rootId) };
        relationships.AddRange(unique
            .Where(item => !JsValue.StrictEquals(item["SPDXID"], rootId))
            .Select(item => Relationship(rootId, "DEPENDS_ON", (string)item["SPDXID"]!)));

        var document = new JsObject();
        document.Set("spdxVersion", "SPDX-2.3");
        document.Set("dataLicense", "CC0-1.0");
        document.Set("SPDXID", "SPDXRef-DOCUMENT");
        document.Set("name", $"{productName}-{version}");
        document.Set("documentNamespace",
            $"https://aetheus.local/sbom/{JsText.EncodeUriComponent(productName)}/{JsText.EncodeUriComponent(version)}/{sourceCommit}");
        var creationInfo = new JsObject();
        creationInfo.Set("created", created);
        creationInfo.Set("creators", new List<object?> { "Tool: Aetheus deterministic package SBOM generator 1.0" });
        document.Set("creationInfo", creationInfo);
        document.Set("packages", unique.Cast<object?>().ToList());
        document.Set("relationships", relationships);
        return document;
    }

    /// <summary><c>[...new Map(packages.map(item =&gt; [item.SPDXID, item])).values()]</c>.</summary>
    private static List<JsObject> UniqueBySpdxId(List<JsObject> packages)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, JsObject>(StringComparer.Ordinal);
        foreach (var item in packages)
        {
            var id = (string)item["SPDXID"]!;
            if (!byId.ContainsKey(id))
                order.Add(id);
            byId[id] = item;
        }
        return order.Select(id => byId[id]).ToList();
    }

    private static JsObject PackageEntry(object? name, object? packageVersion, string id, string ecosystem)
    {
        var entry = new JsObject();
        entry.Set("name", name);
        entry.Set("SPDXID", id);
        entry.Set("versionInfo", packageVersion);
        entry.Set("downloadLocation", "NOASSERTION");
        entry.Set("filesAnalyzed", false);
        entry.Set("licenseConcluded", "NOASSERTION");
        entry.Set("licenseDeclared", "NOASSERTION");
        var purlType = ecosystem == "nuget" ? "nuget" : "npm";
        var locator =
            $"pkg:{purlType}/{JsText.EncodeUriComponent(JsValue.ToJsString(name))}@{JsText.EncodeUriComponent(JsValue.ToJsString(packageVersion))}";
        var reference = new JsObject();
        reference.Set("referenceCategory", "PACKAGE-MANAGER");
        reference.Set("referenceType", "purl");
        reference.Set("referenceLocator", locator);
        entry.Set("externalRefs", new List<object?> { reference });
        return entry;
    }

    private static JsObject Relationship(string element, string type, string related)
    {
        var relationship = new JsObject();
        relationship.Set("spdxElementId", element);
        relationship.Set("relationshipType", type);
        relationship.Set("relatedSpdxElement", related);
        return relationship;
    }

    /// <summary><c>spdxId(name, version)</c>: every UTF-16 code unit outside <c>[A-Za-z0-9.-]</c> of
    /// <c>`${name}-${version}`</c> replaced by <c>-</c>.</summary>
    private static string SpdxId(object? name, object? packageVersion) =>
        "SPDXRef-Package-" + UnsafeSpdxCharacter().Replace($"{JsValue.ToJsString(name)}-{JsValue.ToJsString(packageVersion)}", "-");

    private static string Resolve(string workspace, string path) => Path.GetFullPath(Path.Combine(workspace, path));
}
