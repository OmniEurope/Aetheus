// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>ensure-node-runtime</c> function, port of the former <c>ensure-node-runtime.sh</c> script (see
/// <see cref="PinnedNodeRuntime"/> for the resolution rules). The script printed the executable path on
/// stdout for <c>NODE="$(sh ensure-node-runtime.sh)"</c>; the function publishes it as <c>NODE</c>, and
/// its directory as <c>NODE_BIN_DIR</c>, which the pipelines derived with <c>dirname</c> to put the
/// pinned Node.js first on a test host's <c>PATH</c>.
/// </summary>
public sealed class EnsureNodeRuntimeFunction : IPipelineFunction
{
    private readonly PinnedNodeRuntime _node;

    public EnsureNodeRuntimeFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal EnsureNodeRuntimeFunction(IPipelineFunctionTools tools, PinnedDownload? archive = null) =>
        _node = new PinnedNodeRuntime(tools, archive ?? PinnedNodeRuntime.OfficialArchive);

    public string Name => PipelineFunctionCatalog.EnsureNodeRuntime.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolution = await _node.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
        if (resolution.Executable is null)
            return PipelineFunctionResult.Failure(resolution.Error!);

        await context.Log($"Pinned Node.js: {resolution.Executable}").ConfigureAwait(false);
        context.SetOutput("NODE", resolution.Executable);
        context.SetOutput("NODE_BIN_DIR", Path.GetDirectoryName(resolution.Executable)!);
        return PipelineFunctionResult.Success();
    }
}
