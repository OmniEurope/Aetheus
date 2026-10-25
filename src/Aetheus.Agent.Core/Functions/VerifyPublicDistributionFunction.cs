// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>verify-public-distribution</c> function, port of the <c>--verify</c> mode of
/// <c>scripts/export-public-distribution.mjs</c> (the export mode stays in the script): which recent
/// commit of an internal branch the public checkout is the distribution of. The candidates are the
/// <c>depth</c> commits of the branch made before the publication; for each one the function reads that
/// commit's own <c>deploy/public-distribution/boundary.json</c>, builds the distribution it would export in
/// memory (<see cref="PublicDistributionBuilder"/>) and compares it, byte for byte, with the files of the
/// public <c>HEAD</c>, leaving out on both sides what <c>compareIgnore</c> of the workspace's boundary
/// lists. The lines the script printed are logged as it printed them. Outputs: <c>PUBLIC_VERIFIED</c>
/// (<c>true</c> or <c>false</c>) and <c>PUBLIC_MATCHED_COMMIT</c> (the 40-hex commit, empty when refused).
///
/// Differences, deliberate: the function succeeds when the public repository is refused (the script exited
/// 1), because the caller branches on <c>PUBLIC_VERIFIED</c>; the lines the script wrote to stderr
/// (<c>Refused</c>, <c>Closest</c>, the latter one line per difference) are logged at warning level. The
/// function fails only for an input it cannot use: a public checkout that is not a Git checkout, a depth or
/// branch that is not valid, a workspace without a readable boundary, a Git error, or a branch with no
/// commit before the publication. The archive of a revision is read in memory instead of being extracted
/// by <c>tar</c> to a temporary folder; a symbolic link counts as the target text Git stores. The script
/// treated any <c>boundary.json</c> that could not be parsed as absent; one that parses but lacks a list the
/// export needs is reported as an export that would be refused, where the script failed with a
/// <c>TypeError</c>. The depth is plain decimal digits.
/// </summary>
public sealed class VerifyPublicDistributionFunction : IPipelineFunction
{
    private const string DefaultBranch = "main";
    private const int DefaultDepth = 20;
    private const string BoundaryPath = "deploy/public-distribution/boundary.json";

    private readonly IPipelineFunctionTools _tools;

    public VerifyPublicDistributionFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal VerifyPublicDistributionFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.VerifyPublicDistribution.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var branch = context.Parameters.GetValueOrDefault("branch") is { Length: > 0 } given ? given : DefaultBranch;
        if (!TryReadDepth(context, out var depth))
            return PipelineFunctionResult.Failure("depth must be a positive integer");
        if (branch.StartsWith('-'))
            return PipelineFunctionResult.Failure($"branch '{branch}' is not a branch name");

        var publicRoot = Path.GetFullPath(Path.Combine(context.Workspace, context.Parameters["public_checkout"]));
        if (!Directory.Exists(Path.Combine(publicRoot, ".git")) && !File.Exists(Path.Combine(publicRoot, ".git")))
            return PipelineFunctionResult.Failure($"'{publicRoot}' is not a git checkout");
        var boundaryFile = Path.Combine(context.Workspace, BoundaryPath);
        if (!File.Exists(boundaryFile))
            return PipelineFunctionResult.Failure($"{BoundaryPath} not found in the workspace.");

        // What may differ is the verifier's policy, read here; what the export makes is each commit's own.
        var ignore = PublicDistributionBoundary.ReadCompareIgnore(
            await File.ReadAllTextAsync(boundaryFile, cancellationToken).ConfigureAwait(false));
        var matched = await VerifyAsync(context, publicRoot, branch, depth, ignore, cancellationToken).ConfigureAwait(false);
        context.SetOutput("PUBLIC_VERIFIED", matched is null ? "false" : "true");
        context.SetOutput("PUBLIC_MATCHED_COMMIT", matched ?? string.Empty);
        return PipelineFunctionResult.Success();
    }

    private async Task<string?> VerifyAsync(
        PipelineFunctionContext context, string publicRoot, string branch, int depth,
        IReadOnlyList<string> ignore, CancellationToken cancellationToken)
    {
        var temporary = Directory.CreateTempSubdirectory("aetheus-public-verify.").FullName;
        try
        {
            var git = new PublicDistributionGit(_tools, context);
            var source = new PublicDistributionSource(git, context.Workspace!, temporary);
            return await new PublicDistributionVerifier(git, source, context, context.Workspace!)
                .VerifyAsync(publicRoot, branch, depth, ignore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static bool TryReadDepth(PipelineFunctionContext context, out int depth)
    {
        depth = DefaultDepth;
        return context.Parameters.GetValueOrDefault("depth") is not { Length: > 0 } text
            || (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out depth) && depth >= 1);
    }
}
