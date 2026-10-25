// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>settle-fast-release-transaction</c> function, port of the leftover-transaction guard of
/// the former <c>release-fast-deploy.sh</c> script. A leftover transaction is not always a deployment in
/// flight: run 2240 moved Apache, recorded its release and then lost the step that closes the
/// transaction, and refusing would have locked every later fast deploy behind a directory nobody could
/// remove without a shell on the host. So a transaction whose state is
/// <c>HOST_COMMITTED_PENDING_RELEASE</c>, whose candidate colour is the live colour and whose commit is
/// the recorded one (or no commit is recorded) is closed: <c>COMMITTED</c> written to its state, then
/// the directory removed. Any other leftover state is refused, as the script refuses it.
///
/// Same files read as the script, with the same <c>$(cat ...)</c> meaning (trailing newlines removed,
/// a missing file reads as empty): <c>live-color</c>, <c>source-commit</c> and
/// <c>fast-deployment-transaction/{state,idle,source-commit}</c> in the environment file's directory.
/// The two checks the script made first on the same files are kept, with its messages: the environment
/// file exists, and the live colour is blue or green.
///
/// Differences, deliberate: <c>ENV_FILE</c> is the <c>env_file</c> parameter, defaulting to the same
/// variable. With no leftover transaction the function says so in the log, where the script printed
/// nothing.
/// </summary>
public sealed class SettleFastReleaseTransactionFunction : IPipelineFunction
{
    internal const string CommittedPendingRelease = "HOST_COMMITTED_PENDING_RELEASE";

    public string Name => PipelineFunctionCatalog.SettleFastReleaseTransaction.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var envFile = FunctionShell.Input(context, "env_file", "ENV_FILE");
        if (envFile is null)
            return PipelineFunctionResult.Failure("Required variable is missing: ENV_FILE");
        var stateDirectory = ProductionStatePaths.Dirname(envFile);
        if (!File.Exists(envFile))
            return PipelineFunctionResult.Failure($"Fast release requires an existing production installation and {envFile}.");

        var live = ReadOrEmpty(stateDirectory + "/live-color");
        if (live is not ("blue" or "green"))
            return PipelineFunctionResult.Failure("Fast release requires an existing blue-green live colour.");

        var transaction = stateDirectory + "/fast-deployment-transaction";
        if (!Directory.Exists(transaction) && !File.Exists(transaction))
        {
            await context.Log($">>> No unfinished fast deployment transaction at {transaction}.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }

        var state = ReadOrEmpty(transaction + "/state");
        var idle = ReadOrEmpty(transaction + "/idle");
        var commit = ReadOrEmpty(transaction + "/source-commit");
        var recorded = ReadOrEmpty(stateDirectory + "/source-commit");
        if (state != CommittedPendingRelease || idle != live || (recorded.Length > 0 && recorded != commit))
            return PipelineFunctionResult.Failure(
                $"an unfinished fast deployment transaction already exists at {transaction} (state={OrUnknown(state)}, candidate={OrUnknown(idle)}, live={live}).");

        await context.Log($">>> Closing the fast deployment transaction of {(commit.Length > 0 ? commit : "an unknown commit")}: it went live as {live} and was never committed.").ConfigureAwait(false);
        await File.WriteAllTextAsync(transaction + "/state", "COMMITTED\n", cancellationToken).ConfigureAwait(false);
        Directory.Delete(transaction, recursive: true);
        return PipelineFunctionResult.Success();
    }

    /// <summary><c>$(cat file 2&gt;/dev/null || true)</c>.</summary>
    private static string ReadOrEmpty(string path) =>
        File.Exists(path) ? File.ReadAllText(path).TrimEnd('\n') : "";

    private static string OrUnknown(string value) => value.Length > 0 ? value : "?";
}
