// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>ensure-dotnet-sdk</c> function, port of <c>scripts/ensure-dotnet-sdk.sh</c> (see
/// <see cref="PinnedDotnetSdk"/> for the resolution rules). The script printed the executable path on
/// stdout for <c>DOTNET="$(sh ensure-dotnet-sdk.sh)"</c>; the function publishes it as <c>DOTNET</c>.
/// Its optional argument, the manifest path relative to the working directory, is the
/// <c>global_json</c> parameter here, relative to the workspace.
/// </summary>
public sealed class EnsureDotnetSdkFunction : IPipelineFunction
{
    private readonly PinnedDotnetSdk _sdk;

    public EnsureDotnetSdkFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal EnsureDotnetSdkFunction(IPipelineFunctionTools tools, PinnedDownload? installer = null) =>
        _sdk = new PinnedDotnetSdk(tools, installer ?? PinnedDotnetSdk.OfficialInstaller);

    public string Name => PipelineFunctionCatalog.EnsureDotnetSdk.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var globalJson = context.Parameters.GetValueOrDefault("global_json");
        var resolution = await _sdk
            .ResolveAsync(context, context.Workspace, string.IsNullOrWhiteSpace(globalJson) ? "global.json" : globalJson, cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Executable is null)
            return PipelineFunctionResult.Failure(resolution.Error!);

        await context.Log($"Pinned .NET SDK: {resolution.Executable}").ConfigureAwait(false);
        context.SetOutput("DOTNET", resolution.Executable);
        return PipelineFunctionResult.Success();
    }
}
