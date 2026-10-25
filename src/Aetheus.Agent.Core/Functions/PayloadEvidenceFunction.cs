// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>payload-evidence</c> function, port of the former <c>nightly-evidence.mjs</c> script: describes
/// each artifact as <c>{ name, bytes, sha256 }</c> (the name is the path's last segment, unique, the
/// file regular and not empty, checked in argument order), sorted by name with
/// <see cref="JsLocaleCompare"/>. <c>create</c> writes
/// <c>{ schema: 1, kind, sourceSha, artifacts }</c> as <see cref="JsJson.StringifyIndented"/> plus a line
/// feed (mode 0644 when created on Unix), byte for byte the script's file, so the script's own verify,
/// which the demo prepare step of <c>aetheus-demo.yaml</c> still runs, accepts it. <c>verify</c> describes the artifacts
/// first, then requires the evidence's schema to be the number 1, its kind the expected one, its source
/// the lowercase commit and <c>JSON.stringify</c> of its artifacts to equal that of the recomputed list.
/// Messages and refusals are the script's, verbatim.
///
/// Parameters are the script's arguments, paths relative to the workspace, the artifacts separated by
/// spaces. <c>kind</c>, default <c>nightly-demo-payload</c> (the value the script hard-coded), is the
/// kind written and required.
///
/// Differences, deliberate: an artifact path cannot hold a space; an empty required parameter is
/// refused by the request validation with its own message (the script printed its usage); an artifact
/// that does not exist fails with Node's <c>ENOENT ... stat</c> message rebuilt from the path as given,
/// any other file system error and evidence that is not JSON fail with the .NET or parser message; an
/// artifact name outside ASCII among several is refused (<see cref="JsLocaleCompare"/>); the name is
/// taken after the last <c>/</c> as POSIX <c>basename</c> does, whatever the host.
/// </summary>
public sealed partial class PayloadEvidenceFunction : IPipelineFunction
{
    private const string DefaultKind = "nightly-demo-payload";
    private const string Usage = "Usage: nightly-evidence.mjs <create|verify> <source-sha> <evidence> <artifact>...";
    private const UnixFileMode EvidenceMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex("^[0-9a-fA-F]{40}\\z")]
    private static partial Regex SourceSha();

    public string Name => PipelineFunctionCatalog.PayloadEvidence.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var mode = context.Parameters["mode"];
        var expectedSource = context.Parameters["source_sha"];
        var evidencePath = context.Parameters["evidence"];
        var artifactPaths = context.Parameters["artifacts"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kind = context.Parameters.GetValueOrDefault("kind") is { Length: > 0 } given ? given : DefaultKind;
        if (mode is not ("create" or "verify") || !SourceSha().IsMatch(expectedSource) || evidencePath.Length == 0 || artifactPaths.Length == 0)
            throw new JsError(Usage);

        var artifacts = await DescribeArtifactsAsync(context.Workspace, artifactPaths, cancellationToken).ConfigureAwait(false);
        var sourceSha = JsValue.AsciiLower(expectedSource);
        var evidenceFile = Path.GetFullPath(Path.Combine(context.Workspace, evidencePath));
        if (mode == "create")
        {
            await CreateAsync(evidenceFile, kind, sourceSha, artifacts, cancellationToken).ConfigureAwait(false);
            await context.Log($"Created independent nightly evidence for {sourceSha}.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }

        var evidence = JsJson.Parse(Utf8.GetString(await File.ReadAllBytesAsync(evidenceFile, cancellationToken).ConfigureAwait(false)));
        if (!JsValue.StrictEquals(JsValue.Get(evidence, "schema"), 1d) || !JsValue.StrictEquals(JsValue.Get(evidence, "kind"), kind))
            throw new JsError("The nightly evidence schema or kind is invalid.");
        if (!JsValue.StrictEquals(JsValue.Get(evidence, "sourceSha"), sourceSha))
            throw new JsError("The nightly evidence belongs to another source commit.");
        if (!string.Equals(JsJson.Stringify(JsValue.Get(evidence, "artifacts")), JsJson.Stringify(artifacts), StringComparison.Ordinal))
            throw new JsError("The nightly payload differs from its independent evidence.");
        await context.Log($"Verified independent nightly evidence for {JsValue.ToJsString(JsValue.Get(evidence, "sourceSha"))}.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary><c>describeArtifacts(paths)</c>: one entry per artifact, sorted by name.</summary>
    private static async Task<List<object?>> DescribeArtifactsAsync(string workspace, string[] paths, CancellationToken cancellationToken)
    {
        var entries = new List<JsObject>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var name = Basename(path);
            if (!names.Add(name))
                throw new JsError($"Duplicate nightly artifact name: {name}");
            var file = Stat(Path.GetFullPath(Path.Combine(workspace, path)), path);
            if (file is null || file.Length == 0)
                throw new JsError($"Nightly artifact is empty: {name}");
            var entry = new JsObject();
            entry.Set("name", name);
            entry.Set("bytes", (double)file.Length);
            entry.Set("sha256", await DigestAsync(file.FullName, cancellationToken).ConfigureAwait(false));
            entries.Add(entry);
        }
        return [.. JsLocaleCompare.Sort(entries, entry => (string)entry["name"]!)];
    }

    /// <summary><c>stat(path)</c>, symbolic links followed: the file, or null for anything that exists
    /// but is not a regular file; Node's <c>ENOENT</c> error when nothing is there.</summary>
    private static FileInfo? Stat(string fullPath, string given)
    {
        FileSystemInfo entry = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        if (entry.LinkTarget is not null)
            entry = entry.ResolveLinkTarget(returnFinalTarget: true) ?? entry;
        if (!entry.Exists)
            throw new JsError($"ENOENT: no such file or directory, stat '{given}'");
        return entry as FileInfo;
    }

    private static async Task<string> DigestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>POSIX <c>path.basename</c>: the last segment once trailing slashes are removed.</summary>
    private static string Basename(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
            return string.Empty;
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary><c>writeFile(path, JSON.stringify(evidence, null, 2) + "\n", { mode: 0o644 })</c>: the mode
    /// applies when the file is created, under the umask, as <c>open(2)</c> applies it.</summary>
    private static async Task CreateAsync(string path, string kind, string sourceSha, List<object?> artifacts, CancellationToken cancellationToken)
    {
        var evidence = new JsObject();
        evidence.Set("schema", 1d);
        evidence.Set("kind", kind);
        evidence.Set("sourceSha", sourceSha);
        evidence.Set("artifacts", artifacts);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = EvidenceMode;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(Utf8.GetBytes(JsJson.StringifyIndented(evidence) + "\n"), cancellationToken).ConfigureAwait(false);
    }
}
