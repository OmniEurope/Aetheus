// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>verify-production-state</c> function, port of the refusals <c>deploy/scripts/prod-deploy-prepare.sh</c>
/// makes about the production state directory and secret-zero, without any of its writes. Run before
/// the preparation, it refuses what the preparation would refuse, before anything is loaded or copied:
/// <list type="number">
/// <item><c>ENV_FILE</c> is set; its directory is the declared <c>STATE_DIR</c> when one is declared;</item>
/// <item>that directory contains no <c>..</c>, is a direct child of <c>/var/lib</c>, and is not a
/// symbolic link;</item>
/// <item>neither the environment file nor the legacy one is a symbolic link, and when both exist they
/// are byte for byte identical;</item>
/// <item>when the environment file is missing, with no legacy file to migrate and no dated copy
/// (<c>&lt;file&gt;-YYYYMMDDTHHMMSSZ</c>) to restore, the preparation would generate new secrets: that
/// is refused when persistent state exists (a <c>&lt;project&gt;-{db-data,git-repos,dp-keys,artifacts,packages}</c>
/// volume or a container named <c>&lt;project&gt;-*</c>), since new secrets could never open it.</item>
/// </list>
/// Same messages as the script.
///
/// Differences, deliberate: the script's variables are parameters defaulting to them. A container
/// list that Docker cannot give fails the function, where the script's pipe into <c>grep</c> read a
/// failure as "no container" (the script had already loaded images by then, so Docker was known to
/// answer; this function runs first and must not read an unreachable daemon as a first deploy). The
/// container name prefix is compared literally, where the script used it as an extended regular
/// expression (Compose project names hold no regex metacharacter).
/// </summary>
public sealed class VerifyProductionStateFunction : IPipelineFunction
{
    private static readonly string[] s_persistentResources = ["db-data", "git-repos", "dp-keys", "artifacts", "packages"];

    private readonly IPipelineFunctionTools _tools;
    private readonly string _stateRoot;

    public VerifyProductionStateFunction()
        : this(PipelineFunctionTools.Default, ProductionStatePaths.StateRoot)
    {
    }

    /// <summary>Tests stand a writable directory in for <c>/var/lib</c>; nothing else changes.</summary>
    internal VerifyProductionStateFunction(IPipelineFunctionTools tools, string stateRoot)
    {
        _tools = tools;
        _stateRoot = stateRoot;
    }

    public string Name => PipelineFunctionCatalog.VerifyProductionState.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var envFile = FunctionShell.Input(context, "env_file", "ENV_FILE");
        if (envFile is null)
            return PipelineFunctionResult.Failure("ENV_FILE is required");
        var legacy = FunctionShell.Input(context, "legacy_env_file", "LEGACY_ENV_FILE");
        var refusal = StateDirectoryRefusal(envFile, FunctionShell.Input(context, "state_dir", "STATE_DIR"))
            ?? EnvironmentFileRefusal(envFile, legacy);
        if (refusal is not null)
            return PipelineFunctionResult.Failure(refusal);

        if (File.Exists(envFile))
        {
            await context.Log($">>> {envFile} exists; its secrets will be reused, never regenerated.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }
        if (legacy is not null && File.Exists(legacy))
        {
            await context.Log($">>> {envFile} is missing; the preparation will migrate {legacy}.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }
        if (ProductionStatePaths.HasDatedBackup(envFile))
        {
            await context.Log($">>> {envFile} is missing; the preparation will restore its latest dated copy.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }
        return await FirstDeployAsync(context, envFile, cancellationToken).ConfigureAwait(false);
    }

    private string? StateDirectoryRefusal(string envFile, string? declared)
    {
        var stateDirectory = ProductionStatePaths.Dirname(envFile);
        if (declared is not null && declared != stateDirectory)
            return $"ENV_FILE ({envFile}) does not live in STATE_DIR ({declared}).";
        return ProductionStatePaths.ShapeRefusal(stateDirectory, _stateRoot)
            ?? (ProductionStatePaths.IsSymbolicLink(stateDirectory) ? "Production state directory is symbolic." : null);
    }

    private static string? EnvironmentFileRefusal(string envFile, string? legacy)
    {
        if (ProductionStatePaths.IsSymbolicLink(envFile))
            return "Production environment file must not be a symbolic link.";
        if (legacy is null)
            return null;
        if (ProductionStatePaths.IsSymbolicLink(legacy))
            return "Legacy production environment file must not be a symbolic link.";
        return File.Exists(envFile) && File.Exists(legacy)
            && !File.ReadAllBytes(envFile).AsSpan().SequenceEqual(File.ReadAllBytes(legacy))
            ? "Canonical and legacy production environments differ; refusing automatic selection."
            : null;
    }

    private async Task<PipelineFunctionResult> FirstDeployAsync(
        PipelineFunctionContext context, string envFile, CancellationToken cancellationToken)
    {
        var project = FunctionShell.Input(context, "compose_project", "COMPOSE_PROJECT");
        if (project is null)
            return PipelineFunctionResult.Failure("COMPOSE_PROJECT is required");

        var present = await PersistentStateAsync(context, project, cancellationToken).ConfigureAwait(false);
        if (present is null)
            return PipelineFunctionResult.Failure("docker ps -a failed; persistent production state cannot be ruled out.");
        if (present.Value)
        {
            await context.Log("Secret regeneration is forbidden; no valid dated installation backup was found.", TaskLogLevel.Error).ConfigureAwait(false);
            return PipelineFunctionResult.Failure($"Persistent production state exists but {envFile} is missing.");
        }
        await context.Log($">>> First deploy: no persistent state of {project}; the preparation will generate the secrets at {envFile}.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>Whether a volume or a container of the project exists, or null when Docker cannot list
    /// the containers.</summary>
    private async Task<bool?> PersistentStateAsync(PipelineFunctionContext context, string project, CancellationToken cancellationToken)
    {
        var docker = FunctionShell.Tool(context, "docker");
        var directory = context.Workspace ?? Environment.CurrentDirectory;
        var present = false;
        foreach (var resource in s_persistentResources)
        {
            var inspect = await _tools.RunAsync(
                FunctionShell.StartInfo(context, docker, directory, "volume", "inspect", $"{project}-{resource}"),
                (_, _) => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
            present |= inspect.ExitCode == 0 && !inspect.TimedOut;
        }
        var names = await FunctionShell.CaptureAsync(
            _tools, FunctionShell.StartInfo(context, docker, directory, "ps", "-a", "--format", "{{.Names}}"),
            cancellationToken).ConfigureAwait(false);
        if (names is null)
            return null;
        return present || names.Split('\n').Any(name => name.StartsWith(project + "-", StringComparison.Ordinal));
    }
}
