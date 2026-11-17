// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Shared.Components.VulnerabilityWatch;

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

    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _attemptTimeout;

    public ScanDotnetRuntimeCveFunction()
        : this(FunctionHttp.Shared)
    {
    }

    /// <param name="delay">The wait between two attempts; tests pass one that returns at once.</param>
    /// <param name="attemptTimeout">How long one fetch may take; null for <see cref="DefaultAttemptTimeout"/>.</param>
    internal ScanDotnetRuntimeCveFunction(
        HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? attemptTimeout = null)
    {
        _http = http;
        _delay = delay ?? Task.Delay;
        _attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
    }

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
        if (!IsInside(workspace, output))
            return PipelineFunctionResult.Failure($"The output {output} is outside the workspace.");
        var metadataUrl = context.Parameters.GetValueOrDefault("metadata_url") is { Length: > 0 } custom ? custom : DefaultMetadataUrl;
        if (!Uri.TryCreate(metadataUrl, UriKind.Absolute, out var metadata) || metadata.Scheme is not ("http" or "https"))
            return PipelineFunctionResult.Failure($"The metadata_url '{metadataUrl}' is not an absolute HTTP(S) URL.");
        var input = Path.GetFullPath(context.Parameters.GetValueOrDefault("input") is { Length: > 0 } relative ? relative : ".analysis-image", workspace);
        if (!IsInside(workspace, input))
            return PipelineFunctionResult.Failure($"The input {input} is outside the workspace.");

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

    private static bool IsInside(string workspace, string path) =>
        path.StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

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
                releases = await GetWithRetryAsync(url.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            }
            catch (FunctionHttpException ex)
            {
                throw new FunctionHttpException($"The .NET {version.Channel} release metadata could not be read: {ex.Message}", ex);
            }
            channels[version.Channel] = releases;
        }
        return releases;
    }

    /// <summary>One fetch of the channel's metadata, tried up to <see cref="MaxAttempts"/> times in all on a server error
    /// (5xx), no answer or a timeout; a client error (404...) is final at once. The last failure is the one reported.</summary>
    private async Task<string> GetWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(_attemptTimeout);
            FunctionHttpException failure;
            try
            {
                return await new FunctionHttp(_http).GetAsync(url, null, attemptTimeout.Token).ConfigureAwait(false);
            }
            catch (FunctionHttpException ex) when (ex.StatusCode is null or >= System.Net.HttpStatusCode.InternalServerError)
            {
                failure = ex;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = new FunctionHttpException($"GET {url} timed out after {_attemptTimeout.TotalSeconds:0} seconds.");
            }

            if (attempt >= MaxAttempts)
                throw new FunctionHttpException($"{failure.Message} (after {MaxAttempts} attempts)", failure);
            await _delay(RetryBackoff * attempt, cancellationToken).ConfigureAwait(false);
        }
    }
}
