// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>retain-docker-images</c> function, port of the former <c>retain-docker-images.sh</c> script:
/// every image a container references (running or stopped) is protected, which keeps the live colour
/// and its N-1; for each managed repository the tags are walked newest first, protected and the
/// <c>keep</c> most recent ones are kept, and every other tag is removed, after re-resolving it and the
/// containers right before the removal so a concurrent deployment cannot lose an image it just tagged or
/// started. Same Docker commands, in the same order, and the same lines as the script.
///
/// Differences, deliberate: the script's arguments are the required <c>repositories</c> parameter;
/// <c>AETHEUS_IMAGE_RETENTION_KEEP</c> and <c>AETHEUS_IMAGE_RETENTION_DRY_RUN</c> are the <c>keep</c> and
/// <c>dry_run</c> parameters, defaulting to the same variables and values. The script's
/// <c>flock -n</c> on <c>${TMPDIR:-/tmp}/aetheus-image-retention.lock</c> is an exclusive lock on the
/// same file taken by the runtime (an advisory <c>flock</c> on Linux), so the function and the legacy
/// script still exclude each other, and <c>flock</c> no longer has to be installed. The protected
/// images are held in memory instead of a temporary file. A <c>REMOVE</c> line names the removed tag's
/// own image id, where the script printed whatever its <c>refresh_protected_images</c> had just left
/// in the shared <c>image_id</c> variable (the last container's image, or nothing). A refused
/// <c>docker image rm</c> names its exit code, where the script stopped on <c>set -e</c> silently.
/// </summary>
public sealed partial class RetainDockerImagesFunction : IPipelineFunction
{
    private const string InventoryFormat = "{{.ID}}|{{.Repository}}:{{.Tag}}|{{.CreatedAt}}";

    [GeneratedRegex("^[a-zA-Z0-9._/-]+\\z")]
    private static partial Regex SafeRepository();

    private readonly IPipelineFunctionTools _tools;

    public RetainDockerImagesFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal RetainDockerImagesFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.RetainDockerImages.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var dryRun = FunctionShell.Input(context, "dry_run", "AETHEUS_IMAGE_RETENTION_DRY_RUN") ?? "false";
        var keepText = FunctionShell.Input(context, "keep", "AETHEUS_IMAGE_RETENTION_KEEP") ?? "2";
        var repositories = (context.Parameters.GetValueOrDefault("repositories") ?? string.Empty)
            .Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (dryRun is not ("true" or "false"))
            return PipelineFunctionResult.Failure("AETHEUS_IMAGE_RETENTION_DRY_RUN must be true or false");
        if (keepText.Length == 0 || !keepText.All(char.IsAsciiDigit))
            return PipelineFunctionResult.Failure("AETHEUS_IMAGE_RETENTION_KEEP must be a positive integer");
        var keep = long.TryParse(keepText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : long.MaxValue;
        if (keep < 2)
            return PipelineFunctionResult.Failure("Image retention must keep at least active and N-1");
        if (repositories.Length == 0)
            return PipelineFunctionResult.Failure("At least one managed image repository is required");

        var lockPath = Path.Combine(FunctionShell.Variable(context, "TMPDIR") ?? "/tmp", "aetheus-image-retention.lock");
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(lockPath, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (IOException ex) when (ex is not DirectoryNotFoundException and not PathTooLongException)
        {
            // Mandatory: an exclusive lock another retention holds surfaces only as this exception.
            return PipelineFunctionResult.Failure("Another image-retention operation is active; refusing concurrent retention.");
        }
        await using (lockFile.ConfigureAwait(false))
            return await RetainAsync(
                context, new Retention(new FunctionDocker(_tools, context), context, dryRun == "true", keep), keepText, repositories,
                cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PipelineFunctionResult> RetainAsync(
        PipelineFunctionContext context, Retention retention, string keepText, string[] repositories, CancellationToken cancellationToken)
    {
        if (await retention.RefreshProtectedAsync(cancellationToken).ConfigureAwait(false) is { } refusal)
            return PipelineFunctionResult.Failure(refusal);

        await context.Log($">>> Docker image retention: dryRun={(retention.DryRun ? "true" : "false")} keep={keepText}").ConfigureAwait(false);
        await context.Log(">>> Protected image IDs referenced by active or stopped containers:").ConfigureAwait(false);
        foreach (var image in retention.Protected)
            await context.Log("    " + image).ConfigureAwait(false);

        foreach (var repository in repositories)
        {
            if (repository.StartsWith('-'))
                return PipelineFunctionResult.Failure($"Managed repository must not start with '-': '{repository}'");
            if (!SafeRepository().IsMatch(repository))
                return PipelineFunctionResult.Failure($"Unsafe managed repository '{repository}'");

            await context.Log($">>> Inventory before retention for {repository}").ConfigureAwait(false);
            if (await InventoryAsync(retention.Docker, repository, cancellationToken).ConfigureAwait(false) is { } before)
                return PipelineFunctionResult.Failure(before);
            if (await retention.RetainRepositoryAsync(repository, cancellationToken).ConfigureAwait(false) is { } failure)
                return PipelineFunctionResult.Failure(failure);
            await context.Log($">>> Inventory after retention for {repository}").ConfigureAwait(false);
            if (await InventoryAsync(retention.Docker, repository, cancellationToken).ConfigureAwait(false) is { } after)
                return PipelineFunctionResult.Failure(after);
        }
        return PipelineFunctionResult.Success();
    }

    /// <summary>Lists the repository's images into the step log; null, or the failure that stopped the
    /// script under <c>set -e</c>.</summary>
    private static async Task<string?> InventoryAsync(FunctionDocker docker, string repository, CancellationToken cancellationToken)
    {
        var exitCode = await docker.RunAsync(cancellationToken, "image", "ls", repository, "--no-trunc", "--format", InventoryFormat)
            .ConfigureAwait(false);
        return exitCode == 0 ? null : $"docker image ls {repository} failed with exit code {exitCode}.";
    }

    /// <summary>One retention pass: the protected image ids and the per-repository decisions.</summary>
    private sealed class Retention(FunctionDocker docker, PipelineFunctionContext context, bool dryRun, long keep)
    {
        private const string TagFormat = "{{.ID}}|{{.Repository}}:{{.Tag}}";

        private readonly List<string> _protected = [];

        public FunctionDocker Docker => docker;
        public bool DryRun => dryRun;
        public IReadOnlyList<string> Protected => _protected;

        /// <summary>The script's <c>refresh_protected_images</c>: the image of every container, a
        /// container that vanished since the listing skipped, one that cannot be inspected refused.
        /// Null on success, else the refusal.</summary>
        public async Task<string?> RefreshProtectedAsync(CancellationToken cancellationToken)
        {
            var containers = await docker.LinesAsync(cancellationToken, "ps", "-aq", "--no-trunc").ConfigureAwait(false);
            if (containers is null)
                return "Could not enumerate containers; refusing image retention.";
            _protected.Clear();
            foreach (var container in containers)
            {
                var image = await docker.LinesAsync(cancellationToken, "inspect", "--format", "{{.Image}}", container).ConfigureAwait(false);
                if (image is not null)
                {
                    _protected.Add(string.Join('\n', image));
                    continue;
                }
                var matching = await docker.LinesAsync(cancellationToken, "ps", "-aq", "--no-trunc", "--filter", "id=" + container).ConfigureAwait(false);
                if (matching is null)
                    return $"Could not re-enumerate container '{container}'; refusing image retention.";
                if (matching.Contains(container, StringComparer.Ordinal))
                    return $"Could not inspect container '{container}'; refusing image retention.";
                await context.Log($"SKIP vanished container {container}").ConfigureAwait(false);
            }
            return null;
        }

        /// <summary>Keeps or removes each tag of the repository, newest first. Null on success, else the
        /// refusal.</summary>
        public async Task<string?> RetainRepositoryAsync(string repository, CancellationToken cancellationToken)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var retained = 0;
            var tags = await docker.LinesAsync(cancellationToken, "image", "ls", repository, "--no-trunc", "--format", TagFormat).ConfigureAwait(false) ?? [];
            foreach (var line in tags)
            {
                var fields = line.Split('|');
                var (imageId, tag) = (fields[0], fields.Length > 1 ? fields[1] : string.Empty);
                if (imageId.Length == 0 || tag == repository + ":<none>" || !seen.Add(imageId))
                    continue;

                if (_protected.Contains(imageId, StringComparer.Ordinal))
                {
                    retained++;
                    await context.Log($"KEEP protected {tag} {imageId}").ConfigureAwait(false);
                    continue;
                }
                if (retained < keep)
                {
                    retained++;
                    await context.Log($"KEEP recent {tag} {imageId}").ConfigureAwait(false);
                    continue;
                }
                if (dryRun)
                {
                    await context.Log($"WOULD_REMOVE {tag} {imageId}").ConfigureAwait(false);
                    continue;
                }
                if (await RemoveAsync(tag, imageId, cancellationToken).ConfigureAwait(false) is { } refusal)
                    return refusal;
            }
            return null;
        }

        /// <summary>Re-resolves the tag and the containers immediately before removing it.</summary>
        private async Task<string?> RemoveAsync(string tag, string imageId, CancellationToken cancellationToken)
        {
            var current = await docker.LinesAsync(cancellationToken, "image", "inspect", "--format", "{{.Id}}", tag).ConfigureAwait(false);
            if (current is null)
                return $"Could not re-inspect '{tag}'; refusing image retention.";
            var currentId = string.Join('\n', current);
            if (currentId != imageId)
                return $"Tag '{tag}' changed from {imageId} to {currentId}; refusing stale deletion.";
            if (await RefreshProtectedAsync(cancellationToken).ConfigureAwait(false) is { } refusal)
                return refusal;
            if (_protected.Contains(currentId, StringComparer.Ordinal))
            {
                await context.Log($"KEEP newly-protected {tag} {currentId}").ConfigureAwait(false);
                return null;
            }
            await context.Log($"REMOVE {tag} {imageId}").ConfigureAwait(false);
            var exitCode = await docker.RunAsync(cancellationToken, "image", "rm", tag).ConfigureAwait(false);
            return exitCode == 0 ? null : $"docker image rm {tag} failed with exit code {exitCode}.";
        }
    }
}
