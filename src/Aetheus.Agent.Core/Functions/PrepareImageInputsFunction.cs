// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>prepare-image-inputs</c> function, port of
/// the former <c>prepare-image-inputs.sh</c> script: before the image scanners run, the restored
/// archives must come from the pipeline's revision (<c>source-commit</c>), their delivery contract must
/// describe that same revision (<c>schema</c> 2, <c>sourceSha</c>) and give each archive a SHA-256 they
/// match; only then is each <c>&lt;name&gt;.tar.gz</c> unpacked to <c>.analysis-image/&lt;name&gt;.tar</c>,
/// with the <c>source-commit</c> marker the scanners check. Same checks, same order, same messages for
/// the ones the script worded.
///
/// Differences, deliberate:
/// <list type="bullet">
/// <item>The archives are the required <c>images</c> parameter; the script hard-coded
/// <c>aetheus-back.tar.gz</c> and <c>aetheus-front.tar.gz</c>, the only thing in it specific to this
/// repository.</item>
/// <item>The input directory (<c>input</c>, default <c>.security-image-input/.pipeline-artifacts</c>) and
/// the revision (<c>source</c>, default <c>BUILD_SOURCEVERSION</c>) are parameters with the script's
/// values as defaults.</item>
/// <item>The contract check and the digests are in-process, so Node.js and <c>sha256sum</c> are no
/// longer needed; the refusals the script left to <c>test</c>, <c>tr</c> or <c>sha256sum</c> name their
/// cause here.</item>
/// </list>
/// </summary>
public sealed partial class PrepareImageInputsFunction : IPipelineFunction
{
    internal const string DefaultInput = ".security-image-input/.pipeline-artifacts";
    internal const string ImageDirectoryName = ".analysis-image";
    private const long MaxContractBytes = 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{64}\\z")]
    private static partial Regex Sha256();

    public string Name => PipelineFunctionCatalog.PrepareImageInputs.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var images = (context.Parameters.GetValueOrDefault("images") ?? string.Empty)
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (images.Length == 0)
            return PipelineFunctionResult.Failure("The images parameter is required.");
        if (images.FirstOrDefault(image => !IsArchiveName(image)) is { } invalid)
            return PipelineFunctionResult.Failure($"Image archive '{invalid}' is not a plain .tar.gz file name.");
        var expected = FunctionShell.Input(context, "source", "BUILD_SOURCEVERSION");
        if (expected is null)
            return PipelineFunctionResult.Failure("BUILD_SOURCEVERSION is not set: the revision the images must come from is unknown.");

        var input = Path.Combine(context.Workspace, context.Parameters.GetValueOrDefault("input") is { Length: > 0 } relative ? relative : DefaultInput);
        var marker = Path.Combine(input, "source-commit");
        if (!File.Exists(marker))
            return PipelineFunctionResult.Failure($"The restored image artifact has no source-commit at {marker}.");
        var actual = (await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false)).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        if (actual != expected)
            return PipelineFunctionResult.Failure($"Security image artifact revision {actual} does not match pipeline revision {expected}.");
        if (images.FirstOrDefault(image => !HasContent(Path.Combine(input, image))) is { } missing)
            return PipelineFunctionResult.Failure($"Security image archive {Path.Combine(input, missing)} is missing or empty.");

        var digests = await ReadContractDigestsAsync(Path.Combine(input, "delivery-contract.json"), expected, images, cancellationToken).ConfigureAwait(false);
        if (digests.Failure is not null)
            return PipelineFunctionResult.Failure(digests.Failure);
        foreach (var image in images)
        {
            if (!await FunctionShell.HasSha256Async(Path.Combine(input, image), digests.Values![image], cancellationToken).ConfigureAwait(false))
                return PipelineFunctionResult.Failure($"Security image '{image}' does not match its delivery contract digest.");
            await context.Log($"{Path.Combine(input, image)}: OK").ConfigureAwait(false);
        }

        return await UnpackAsync(context, context.Workspace, input, images, actual, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PipelineFunctionResult> UnpackAsync(
        PipelineFunctionContext context, string workspace, string input, string[] images, string revision, CancellationToken cancellationToken)
    {
        var output = Path.Combine(workspace, ImageDirectoryName);
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);
        foreach (var image in images)
        {
            var tar = Path.Combine(output, image[..^".gz".Length]);
            try
            {
                await using var source = File.OpenRead(Path.Combine(input, image));
                await using var gzip = new GZipStream(source, CompressionMode.Decompress);
                await using var destination = File.Create(tar);
                await gzip.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                return PipelineFunctionResult.Failure($"Security image '{image}' could not be decompressed: {ex.Message}");
            }
            if (!HasContent(tar))
                return PipelineFunctionResult.Failure($"Security image '{image}' decompressed to an empty archive.");
            await context.Log($"Unpacked {image} to {ImageDirectoryName}/{Path.GetFileName(tar)}.").ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "source-commit"), revision + "\n", cancellationToken).ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>The script's inline Node check: the contract belongs to <paramref name="expected"/> and
    /// gives every archive a lowercase SHA-256.</summary>
    private static async Task<(IReadOnlyDictionary<string, string>? Values, string? Failure)> ReadContractDigestsAsync(
        string path, string expected, string[] images, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > MaxContractBytes)
            return (null, $"The security delivery contract {path} is missing or implausibly large.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException ex)
        {
            return (null, $"The security delivery contract is not valid JSON: {ex.Message}");
        }
        using (document)
        {
            var root = document.RootElement;
            if (!ContractBelongsTo(root, expected))
                return (null, "The security delivery contract belongs to another source commit.");
            var digests = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var image in images)
            {
                var digest = ContractDigest(root, image);
                if (digest is null || !Sha256().IsMatch(digest))
                    return (null, $"Security image '{image}' has no valid delivery contract digest.");
                digests[image] = digest;
            }
            return (digests, null);
        }
    }

    private static bool ContractBelongsTo(JsonElement root, string expected) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.GetDouble() == 2
        && root.TryGetProperty("sourceSha", out var source) && source.ValueKind == JsonValueKind.String && source.GetString() == expected;

    private static string? ContractDigest(JsonElement root, string image) =>
        root.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Object
        && artifacts.TryGetProperty(image, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsArchiveName(string name) =>
        name == Path.GetFileName(name) && name.EndsWith(".tar.gz", StringComparison.Ordinal)
        && name.Length > ".tar.gz".Length && !name.Contains("..", StringComparison.Ordinal)
        && !name.Contains('\\', StringComparison.Ordinal) && !name.Contains('/', StringComparison.Ordinal);

    private static bool HasContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
}
