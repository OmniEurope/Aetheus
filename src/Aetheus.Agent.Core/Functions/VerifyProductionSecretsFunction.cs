// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>verify-production-secrets</c> function, port of the required-secret check of
/// the former <c>prod-deploy-prepare.sh</c> script, run after the preparation: every listed key has a
/// non-empty line (<c>grep -q "^KEY=.\+"</c>) in the environment file, with the script's message for
/// the first one that does not. It also requires what the preparation guarantees just after that check:
/// a dated recovery copy <c>&lt;file&gt;-YYYYMMDDTHHMMSSZ</c> next to the environment file, which the
/// first deploy creates and every reinstall adds to.
///
/// Differences, deliberate: the key list is the required <c>keys</c> parameter instead of the Aetheus
/// list written in the script (<c>APPNAME ENV DB_USER DB_PASSWORD JWT_KEY ADMIN_PASSWORD ENCRYPTION_KEY
/// ENCRYPTION_SALT DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY</c>), and <c>ENV_FILE</c> is the <c>env_file</c>
/// parameter, defaulting to the same variable. A missing or symbolic environment file is refused with
/// its own message, where the script had already refused both earlier. A missing dated copy is a
/// refusal of its own: the script created it rather than checking it.
/// </summary>
public sealed class VerifyProductionSecretsFunction : IPipelineFunction
{
    public string Name => PipelineFunctionCatalog.VerifyProductionSecrets.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var envFile = FunctionShell.Input(context, "env_file", "ENV_FILE");
        if (envFile is null)
            return PipelineFunctionResult.Failure("ENV_FILE is required");
        // Required and non-blank: the executor refuses the step as invalid-request otherwise.
        var keys = context.Parameters["keys"].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ProductionStatePaths.IsSymbolicLink(envFile))
            return PipelineFunctionResult.Failure("Production environment file must not be a symbolic link.");
        if (!File.Exists(envFile))
            return PipelineFunctionResult.Failure($"{envFile} is missing.");

        var lines = (await File.ReadAllTextAsync(envFile, cancellationToken).ConfigureAwait(false)).Split('\n');
        var missing = keys.FirstOrDefault(key => !lines.Any(line => line.Length > key.Length + 1
            && line.StartsWith(key + "=", StringComparison.Ordinal)));
        if (missing is not null)
            return PipelineFunctionResult.Failure($"{missing} is missing or empty in {envFile}.");
        if (!ProductionStatePaths.HasDatedBackup(envFile))
            return PipelineFunctionResult.Failure($"No dated recovery copy of {envFile} exists next to it.");

        await context.Log($">>> {envFile} holds {keys.Length} required keys and a dated recovery copy.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }
}
