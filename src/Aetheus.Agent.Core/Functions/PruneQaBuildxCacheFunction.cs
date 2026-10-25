// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Numerics;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>prune-qa-buildx-cache</c> function, port of the former <c>prune-qa-buildx-cache.sh</c> script:
/// bounds one named Buildx builder's cache with <c>docker buildx prune --builder &lt;name&gt; --force
/// --reserved-space --max-used-space --min-free-space</c>, refusing an unscoped prune (no builder) and
/// limits that are not positive integers or whose maximum is below the reserved size. Same checks, same
/// messages, same command as the script.
///
/// Differences, deliberate: the variables the script read are the <c>builder</c>,
/// <c>max_cache_gib</c>, <c>reserved_cache_gib</c> and <c>min_free_gib</c> parameters, defaulting to the
/// same variables and values. A failed prune names its exit code, where the script stopped on
/// <c>set -e</c> with Docker's output only.
/// </summary>
public sealed class PruneQaBuildxCacheFunction : IPipelineFunction
{
    private readonly IPipelineFunctionTools _tools;

    public PruneQaBuildxCacheFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal PruneQaBuildxCacheFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.PruneQaBuildxCache.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var builder = FunctionShell.Input(context, "builder", "AETHEUS_BUILDX_BUILDER")
            ?? FunctionShell.Variable(context, "BUILDX_BUILDER");
        var max = FunctionShell.Input(context, "max_cache_gib", "AETHEUS_QA_BUILDX_MAX_CACHE_GIB") ?? "32";
        var reserved = FunctionShell.Input(context, "reserved_cache_gib", "AETHEUS_QA_BUILDX_RESERVED_CACHE_GIB") ?? "20";
        var minFree = FunctionShell.Input(context, "min_free_gib", "AETHEUS_QA_BUILDX_MIN_FREE_GIB") ?? "20";

        if (builder is null)
            return PipelineFunctionResult.Failure("AETHEUS_BUILDX_BUILDER is missing; refusing an unscoped Buildx prune.");
        if (!new[] { max, reserved, minFree }.All(IsPositiveLimit))
            return PipelineFunctionResult.Failure("QA Buildx cache limits must be positive integers.");
        if (Parse(max) < Parse(reserved))
            return PipelineFunctionResult.Failure("QA Buildx maximum cache must be greater than or equal to its reserved cache.");

        await context.Log(
            $"Applying bounded QA Buildx maintenance: builder={builder} max={max}Go reserved={reserved}Go minFree={minFree}Go")
            .ConfigureAwait(false);
        var exitCode = await new FunctionDocker(_tools, context).RunAsync(
            cancellationToken,
            "buildx", "prune", "--builder", builder, "--force",
            "--reserved-space", reserved + "GB",
            "--max-used-space", max + "GB",
            "--min-free-space", minFree + "GB").ConfigureAwait(false);
        if (exitCode != 0)
            return PipelineFunctionResult.Failure($"docker buildx prune failed with exit code {exitCode}.");

        await context.Log($"Bounded QA Buildx maintenance completed for '{builder}'.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>The script's <c>''|*[!0-9]*|0</c> refusal: digits only and not the single digit 0.</summary>
    private static bool IsPositiveLimit(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) && value != "0";

    private static BigInteger Parse(string digits) => BigInteger.Parse(digits, CultureInfo.InvariantCulture);
}
