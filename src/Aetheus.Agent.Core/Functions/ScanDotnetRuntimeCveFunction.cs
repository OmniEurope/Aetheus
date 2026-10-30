// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>scan-dotnet-runtime-cve</c> function (recette R-90): the Security stage must report a CVE of the .NET runtime
/// the day Microsoft ships its fix. Trivy cannot: it reads the runtime pack an image lists (it did for
/// <c>aspnet:10.0.13</c>) but answers from GitHub advisories, and those had no entry for CVE-2026-111497, published
/// with 10.0.14 on 2026-10-25, four days later. Microsoft's release metadata does list it, so this function reads the
/// runtime an image ships and the metadata of its channel, and writes one SARIF finding per CVE fixed by a newer release,
/// for the <c>lint</c> step that follows to publish as a container finding the security gate counts.
///
/// Any doubt fails the step, never passes it: an image that cannot be read, a metadata source that cannot be reached or
/// that is not the channel's release list. No report is written then, so an unreadable source is never read as "no CVE".
/// An image that ships no .NET runtime is a valid, empty result.
/// </summary>
public sealed class ScanDotnetRuntimeCveFunction : IPipelineFunction
{
    internal const string DefaultMetadataUrl = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata";

    private readonly HttpClient _http;

    public ScanDotnetRuntimeCveFunction()
        : this(FunctionHttp.Shared)
    {
    }

    internal ScanDotnetRuntimeCveFunction(HttpClient http) => _http = http;

    public string Name => PipelineFunctionCatalog.ScanDotnetRuntimeCve.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var images = (context.Parameters.GetValueOrDefault("images") ?? string.Empty)
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (images.Length == 0)
            return PipelineFunctionResult.Failure("The images parameter is required.");
        if (images.FirstOrDefault(image => image != Path.GetFileName(image) || !image.EndsWith(".tar", StringComparison.Ordinal)) is { } invalid)
            return PipelineFunctionResult.Failure($"Image archive '{invalid}' is not a plain .tar file name.");
        var workspace = Path.GetFullPath(context.Workspace);
        var output = Path.GetFullPath(context.Parameters["output"], workspace);
        if (!output.StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return PipelineFunctionResult.Failure($"The output {output} is outside the workspace.");
        var metadataUrl = context.Parameters.GetValueOrDefault("metadata_url") is { Length: > 0 } custom ? custom : DefaultMetadataUrl;
        if (!Uri.TryCreate(metadataUrl, UriKind.Absolute, out var metadata) || metadata.Scheme is not ("http" or "https"))
            return PipelineFunctionResult.Failure($"The metadata_url '{metadataUrl}' is not an absolute HTTP(S) URL.");
        var input = Path.Combine(workspace, context.Parameters.GetValueOrDefault("input") is { Length: > 0 } relative ? relative : ".analysis-image");

        try
        {
            var findings = new List<DotnetRuntimeCveFinding>();
            var channels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var image in images)
                findings.AddRange(await ScanImageAsync(context, Path.Combine(input, image), image, metadataUrl, channels, cancellationToken).ConfigureAwait(false));

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, DotnetRuntimeCveSarif.Build(findings) + "\n", cancellationToken).ConfigureAwait(false);
            await context.Log($"{findings.Count} .NET runtime CVE finding(s) from {images.Length} image(s), written to {context.Parameters["output"]}.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }
        catch (Exception ex) when (ex is FunctionHttpException or InvalidDataException or FileNotFoundException)
        {
            return PipelineFunctionResult.Failure(ex.Message);
        }
    }

    private async Task<IReadOnlyList<DotnetRuntimeCveFinding>> ScanImageAsync(
        PipelineFunctionContext context,
        string archive,
        string image,
        string metadataUrl,
        Dictionary<string, string> channels,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archive))
            throw new FileNotFoundException($"The image archive {archive} does not exist.");
        var packs = ContainerImageDepsFiles.Read(archive, cancellationToken)
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .SelectMany(file => DotnetRuntimePacks.Read(file.Key, file.Value))
            .ToList();
        if (packs.Count == 0)
        {
            await context.Log($"{image}: no .NET runtime pack found, nothing to compare.").ConfigureAwait(false);
            return [];
        }

        var affected = new Dictionary<string, (DotnetRuntimeAdvisory Advisory, List<DotnetRuntimePack> Packs)>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            var advisories = DotnetReleaseAdvisories.Affecting(
                pack.Version, await ReleasesAsync(metadataUrl, pack.Version, channels, cancellationToken).ConfigureAwait(false));
            await context.Log($"{image}: {pack.Package} {pack.Version} ({pack.DepsPath}): {advisories.Count} CVE(s) fixed by a newer release.").ConfigureAwait(false);
            foreach (var advisory in advisories)
            {
                if (!affected.TryGetValue(advisory.CveId, out var entry))
                    affected[advisory.CveId] = entry = (advisory, []);
                entry.Packs.Add(pack);
            }
        }
        return [.. affected.Values.Select(entry => new DotnetRuntimeCveFinding(image, entry.Packs, entry.Advisory))];
    }

    private async Task<string> ReleasesAsync(
        string metadataUrl, DotnetVersion version, Dictionary<string, string> channels, CancellationToken cancellationToken)
    {
        if (!channels.TryGetValue(version.Channel, out var releases))
        {
            var url = DotnetReleaseAdvisories.ChannelUrl(metadataUrl, version);
            try
            {
                releases = await new FunctionHttp(_http).GetAsync(url.AbsoluteUri, null, cancellationToken).ConfigureAwait(false);
            }
            catch (FunctionHttpException ex)
            {
                throw new FunctionHttpException($"The .NET {version.Channel} release metadata could not be read: {ex.Message}", ex);
            }
            channels[version.Channel] = releases;
        }
        return releases;
    }
}
