// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>extract-agent-release</c> function, port of
/// <c>deploy/scripts/extract-agent-release-from-image.sh</c>: the agent release a backend image serves
/// is copied out of a stopped container (<c>docker create</c>, <c>docker cp</c>, the container always
/// removed), then verified (<see cref="AgentReleaseManifestCheck"/>): the manifest names the expected
/// commit, satisfies the protocol policy (<c>upgrade-bridge</c>: protocol 2 with the 1-2 bridge;
/// <c>historical-compatible</c>: the bridge or 2-2), publishes the <c>ai.run</c> capability, and every
/// archive it lists has its declared size and SHA-256, with the script's messages. <c>manifest-only</c>
/// then removes every other file and the emptied directories.
///
/// Differences, deliberate: the script's positional arguments are parameters; the commit defaults to
/// <c>BUILD_SOURCEVERSION</c>; the directory inside the image is the optional <c>source</c> parameter,
/// <c>/app/wwwroot/downloads/</c> by default; the output directory must stay inside the workspace (the
/// script emptied any path it was given); and the verification the script ran as inline Node.js is C#
/// here, so its <c>node</c> argument is gone.
/// </summary>
public sealed class ExtractAgentReleaseFunction : IPipelineFunction
{
    internal const string ManifestName = AgentReleaseManifest.FileName;
    internal const string DefaultSource = "/app/wwwroot/downloads/";

    private readonly IPipelineFunctionTools _tools;

    public ExtractAgentReleaseFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal ExtractAgentReleaseFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.ExtractAgentRelease.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var request = Request.From(context);
        if (request.Refusal(context.Workspace) is { } refusal)
            return PipelineFunctionResult.Failure(refusal);

        if (Directory.Exists(request.Output))
            Directory.Delete(request.Output, recursive: true);
        Directory.CreateDirectory(request.Output);
        if (await CopyOutAsync(context, request, cancellationToken).ConfigureAwait(false) is { } copyFailure)
            return PipelineFunctionResult.Failure(copyFailure);

        var problem = await AgentReleaseManifestCheck
            .VerifyAsync(request.Output, request.ExpectedCommit, request.ProtocolPolicy, cancellationToken)
            .ConfigureAwait(false);
        if (problem is not null)
            return PipelineFunctionResult.Failure(problem);

        if (request.Mode == "manifest-only")
            KeepManifestOnly(request.Output);
        var manifest = new FileInfo(Path.Combine(request.Output, ManifestName));
        if (!manifest.Exists || manifest.Length == 0)
            return PipelineFunctionResult.Failure($"{ManifestName} is missing or empty after the extraction.");

        await context.Log($"Agent release of {request.Image} extracted and verified in {request.Output}.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary><c>docker create</c>, <c>docker cp</c> of the source directory, then <c>docker rm -f</c>
    /// of the container whether or not the copy worked. Null when the copy succeeded.</summary>
    private async Task<string?> CopyOutAsync(PipelineFunctionContext context, Request request, CancellationToken cancellationToken)
    {
        var docker = new FunctionDocker(_tools, context);
        var created = await docker.LinesAsync(cancellationToken, "create", request.Image).ConfigureAwait(false);
        if (created is not [var container])
            return $"docker create {request.Image} failed.";
        try
        {
            var copied = await docker.RunAsync(cancellationToken, "cp", $"{container}:{request.Source.TrimEnd('/')}/.", request.Output)
                .ConfigureAwait(false);
            return copied == 0 ? null : $"docker cp of {request.Source} out of {request.Image} failed (exit code {copied}).";
        }
        finally
        {
            await docker.QuietAsync(CancellationToken.None, ["rm", "-f", container], keepErrors: false).ConfigureAwait(false);
        }
    }

    /// <summary><c>find -type f ! -name agent-release-manifest.json -delete</c>, then
    /// <c>find -depth -type d -empty -delete</c>.</summary>
    private static void KeepManifestOnly(string output)
    {
        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).ToList())
            if (!string.Equals(Path.GetFileName(file), ManifestName, StringComparison.Ordinal))
                File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(output, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length).ToList())
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
    }

    private sealed record Request(string Image, string Source, string Output, string ExpectedCommit, string Mode, string ProtocolPolicy)
    {
        public static Request From(PipelineFunctionContext context) => new(
            context.Parameters["image"],
            Optional(context, "source") ?? DefaultSource,
            Path.GetFullPath(context.Parameters["output"], context.Workspace!),
            FunctionShell.Input(context, "commit", "BUILD_SOURCEVERSION") ?? string.Empty,
            Optional(context, "mode") ?? "full",
            Optional(context, "protocol_policy") ?? "upgrade-bridge");

        /// <summary>The script's argument checks, with its messages and in its order, then the
        /// workspace bound on the output directory.</summary>
        public string? Refusal(string workspace)
        {
            if (Mode is not ("full" or "manifest-only"))
                return "Output mode must be full or manifest-only.";
            if (ProtocolPolicy is not ("upgrade-bridge" or "historical-compatible"))
                return "Protocol policy must be upgrade-bridge or historical-compatible.";
            if (ExpectedCommit.Length == 0 || !ExpectedCommit.All(char.IsAsciiHexDigit))
                return "Expected source commit must be hexadecimal.";
            var root = Path.GetFullPath(workspace);
            return Output.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? null
                : $"The output directory must stay inside the workspace: {Output}";
        }

        private static string? Optional(PipelineFunctionContext context, string name) =>
            context.Parameters.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;
    }
}
