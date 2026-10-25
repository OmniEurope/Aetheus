// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>bluegreen-host-reset</c> function, port of the former <c>bluegreen-host-reset.sh</c> script:
/// resets a disposable blue-green environment so each qualification starts from fresh containers,
/// volumes and secrets, while vhosts and certificates stay for the next deployment. The identity guards
/// come first (state directory a direct child of the workspace, environment and secrets inside it, a
/// <c>*-demo</c> or <c>*-nightly</c> Compose project, no production token anywhere), then the
/// containers (by Compose label and by the <c>&lt;project&gt;-</c> name prefix), the volumes and the
/// networks of the project are removed, refusing a selection that names production, then the state
/// files, and the reset is proved: no labelled container or volume and no deployment state may remain.
/// Same Docker commands, in the same order, and the same messages as the script.
///
/// Differences, deliberate: <c>STATE_DIR</c>, <c>ENV_FILE</c>, <c>SECRETS_FILE</c> and
/// <c>COMPOSE_PROJECT</c> are parameters defaulting to the same variables. The script's
/// <c>REMOVE_BROWSER_SMOKE_IMAGES=true</c>, which removed the Aetheus <c>&lt;project&gt;-browser-smoke</c>
/// images, is the <c>remove_images</c> list, each entry bounded to the project's name prefix and free of
/// production tokens like every other selection. A Docker listing that fails is a failure, where the
/// script's command substitution read it as empty, which could let the final proof pass on a daemon it
/// never reached. A refused removal names its exit code, where the script stopped on <c>set -e</c>
/// with Docker's output only. Names are sorted ordinally, as <c>sort -u</c> does under the C locale.
/// </summary>
public sealed partial class BlueGreenHostResetFunction : IPipelineFunction
{
    private const string ProjectLabel = "label=com.docker.compose.project=";

    private readonly IPipelineFunctionTools _tools;

    public BlueGreenHostResetFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal BlueGreenHostResetFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.BlueGreenHostReset.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var identity = ResetIdentity.Resolve(context, out var refusal);
        if (identity is null)
            return PipelineFunctionResult.Failure(refusal!);
        var docker = new FunctionDocker(_tools, context);
        if (!docker.IsAvailable)
            return PipelineFunctionResult.Failure("docker is required to reset the environment.");

        var failure = await RemoveContainersAsync(context, docker, identity.Project, cancellationToken).ConfigureAwait(false)
            ?? await RemoveVolumesAsync(context, docker, identity.Project, cancellationToken).ConfigureAwait(false)
            ?? await RemoveNetworksAsync(docker, identity.Project, cancellationToken).ConfigureAwait(false)
            ?? await RemoveImagesAsync(docker, identity.Images, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return PipelineFunctionResult.Failure(failure);

        // A fresh run workspace has no state directory yet: nothing to delete, like the script's rm -f.
        if (Directory.Exists(identity.StateDir))
        {
            File.Delete(identity.EnvFile);
            File.Delete(identity.SecretsFile);
            File.Delete(Path.Combine(identity.StateDir, "live-color"));
            File.Delete(Path.Combine(identity.StateDir, "source-commit"));
            if (Directory.Exists(Path.Combine(identity.StateDir, "deployment-transaction")))
                Directory.Delete(Path.Combine(identity.StateDir, "deployment-transaction"), recursive: true);
        }

        var proof = await ProveAsync(docker, identity, cancellationToken).ConfigureAwait(false);
        if (proof is not null)
            return PipelineFunctionResult.Failure(proof);
        await context.Log($"Environment {identity.Project} reset: no container, no volume, no deployment state.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    private static async Task<string?> RemoveContainersAsync(
        PipelineFunctionContext context, FunctionDocker docker, string project, CancellationToken cancellationToken)
    {
        var labelled = await docker.LinesAsync(cancellationToken, "ps", "-aq", "--filter", ProjectLabel + project).ConfigureAwait(false);
        var named = await docker.LinesAsync(cancellationToken, "ps", "-a", "--format", "{{.ID}} {{.Names}}").ConfigureAwait(false);
        if (labelled is null || named is null)
            return $"Docker could not list the containers of {project}; refusing to delete anything.";
        var containers = Unique(labelled.Concat(named
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length > 1 && fields[1].StartsWith(project + "-", StringComparison.Ordinal))
            .Select(fields => fields[0])));
        if (containers.Length == 0)
            return null;

        var names = await docker.LinesAsync(cancellationToken, ["inspect", "--format", "{{.Name}}", .. containers]).ConfigureAwait(false) ?? [];
        if (names.Any(ResetIdentity.NamesProduction))
            return "A container selected for removal names production; refusing to delete anything.";
        await context.Log($">>> Removing the containers of {project}").ConfigureAwait(false);
        var exitCode = await docker.QuietAsync(cancellationToken, ["rm", "-f", .. containers]).ConfigureAwait(false);
        return exitCode == 0 ? null : $"docker rm -f failed with exit code {exitCode}.";
    }

    private static async Task<string?> RemoveVolumesAsync(
        PipelineFunctionContext context, FunctionDocker docker, string project, CancellationToken cancellationToken)
    {
        var labelled = await docker.LinesAsync(cancellationToken, "volume", "ls", "-q", "--filter", ProjectLabel + project).ConfigureAwait(false);
        var all = await docker.LinesAsync(cancellationToken, "volume", "ls", "-q").ConfigureAwait(false);
        if (labelled is null || all is null)
            return $"Docker could not list the volumes of {project}; refusing to delete anything.";
        var volumes = Unique(labelled.Concat(all.Where(volume => volume.StartsWith(project + "-", StringComparison.Ordinal))));
        if (volumes.Length == 0)
            return null;

        if (volumes.Any(ResetIdentity.NamesProduction))
            return "A volume selected for removal names production; refusing to delete it.";
        await context.Log($">>> Removing the volumes of {project}: {string.Concat(volumes.Select(volume => volume + " "))}").ConfigureAwait(false);
        var exitCode = await docker.QuietAsync(cancellationToken, ["volume", "rm", .. volumes]).ConfigureAwait(false);
        return exitCode == 0 ? null : $"docker volume rm failed with exit code {exitCode}.";
    }

    private static async Task<string?> RemoveNetworksAsync(FunctionDocker docker, string project, CancellationToken cancellationToken)
    {
        var networks = await docker.LinesAsync(cancellationToken, "network", "ls", "-q", "--filter", ProjectLabel + project).ConfigureAwait(false);
        if (networks is null)
            return $"Docker could not list the networks of {project}; refusing to delete anything.";
        if (networks.Count == 0)
            return null;
        var exitCode = await docker.QuietAsync(cancellationToken, ["network", "rm", .. networks]).ConfigureAwait(false);
        return exitCode == 0 ? null : $"docker network rm failed with exit code {exitCode}.";
    }

    /// <summary>The listed images, removed best effort like the script's
    /// <c>docker image rm -f ... || true</c>.</summary>
    private static async Task<string?> RemoveImagesAsync(FunctionDocker docker, IReadOnlyList<string> repositories, CancellationToken cancellationToken)
    {
        foreach (var repository in repositories)
        {
            var images = Unique(await docker.LinesAsync(cancellationToken, "image", "ls", "-q", repository).ConfigureAwait(false) ?? []);
            if (images.Length > 0)
                await docker.QuietAsync(cancellationToken, ["image", "rm", "-f", .. images], keepErrors: false).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<string?> ProveAsync(FunctionDocker docker, ResetIdentity identity, CancellationToken cancellationToken)
    {
        var project = identity.Project;
        if (await docker.LinesAsync(cancellationToken, "ps", "-aq", "--filter", ProjectLabel + project).ConfigureAwait(false) is not { Count: 0 })
            return $"Containers of {project} survived the reset.";
        if (await docker.LinesAsync(cancellationToken, "volume", "ls", "-q", "--filter", ProjectLabel + project).ConfigureAwait(false) is not { Count: 0 })
            return $"Volumes of {project} survived the reset.";
        return Path.Exists(identity.EnvFile) || Path.Exists(Path.Combine(identity.StateDir, "live-color"))
            ? "The deployment state survived the reset."
            : null;
    }

    private static string[] Unique(IEnumerable<string> values) => [.. values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The checked identity of the environment to reset.</summary>
    private sealed partial record ResetIdentity(string StateDir, string EnvFile, string SecretsFile, string Project, IReadOnlyList<string> Images)
    {
        [GeneratedRegex("^[a-z0-9][a-z0-9-]*\\z")]
        private static partial Regex ComposeProject();

        /// <summary>True for a name carrying the production token, which no reset ever touches.</summary>
        public static bool NamesProduction(string value) => value.Contains("prod", StringComparison.OrdinalIgnoreCase);

        /// <summary>The script's guards, in its order, with its messages; null and the refusal when one
        /// fails.</summary>
        public static ResetIdentity? Resolve(PipelineFunctionContext context, out string? refusal)
        {
            var stateDir = FunctionShell.Input(context, "state_dir", "STATE_DIR");
            var envFile = FunctionShell.Input(context, "env_file", "ENV_FILE");
            var secretsFile = FunctionShell.Input(context, "secrets_file", "SECRETS_FILE");
            var project = FunctionShell.Input(context, "compose_project", "COMPOSE_PROJECT");
            var workspace = context.Workspace;
            var images = (context.Parameters.GetValueOrDefault("remove_images") ?? string.Empty)
                .Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
            refusal = (stateDir, envFile, secretsFile, project, workspace) switch
            {
                (null, _, _, _, _) => "STATE_DIR is required",
                (_, null, _, _, _) => "ENV_FILE is required",
                (_, _, null, _, _) => "SECRETS_FILE is required",
                (_, _, _, null, _) => "COMPOSE_PROJECT is required",
                (_, _, _, _, null) => "WORKSPACE is required",
                _ => CheckPaths(workspace, stateDir, envFile, secretsFile) ?? CheckProject(stateDir, envFile, secretsFile, project, images)
            };
            return refusal is null ? new ResetIdentity(stateDir!, envFile!, secretsFile!, project!, images) : null;
        }

        private static string? CheckPaths(string workspace, string stateDir, string envFile, string secretsFile)
        {
            if (!Path.IsPathFullyQualified(workspace))
                return $"WORKSPACE must be an absolute path: {workspace}";
            if (stateDir.Contains("..", StringComparison.Ordinal))
                return $"The state directory must not contain a traversal: {stateDir}";
            var child = stateDir.StartsWith(workspace + "/", StringComparison.Ordinal) ? stateDir[(workspace.Length + 1)..] : null;
            if (child is { Length: > 0 } && child.Contains('/', StringComparison.Ordinal))
                return $"The state directory must be a direct child of the run workspace: {stateDir}";
            if (child is not { Length: > 0 })
                return $"The state directory must live in the run workspace ({workspace}): {stateDir}";
            if (!envFile.StartsWith(stateDir + "/", StringComparison.Ordinal))
                return "The environment file must live in the state directory.";
            return secretsFile.StartsWith(stateDir + "/", StringComparison.Ordinal)
                ? null
                : "The secrets file must live in the state directory.";
        }

        private static string? CheckProject(string stateDir, string envFile, string secretsFile, string project, string[] images)
        {
            if (!ComposeProject().IsMatch(project))
                return $"Not a Compose project name: '{project}'";
            if ($"{stateDir}|{envFile}|{secretsFile}|{project}".Contains("prod", StringComparison.Ordinal))
                return "A reset identity contains a production token; refusing to delete anything.";
            if (!project.EndsWith("-demo", StringComparison.Ordinal) && !project.EndsWith("-nightly", StringComparison.Ordinal))
                return $"Only a disposable project (*-demo or *-nightly) is reset, not '{project}'.";
            var foreign = images.FirstOrDefault(image =>
                !image.StartsWith(project + "-", StringComparison.Ordinal) || NamesProduction(image)
                || image.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/')));
            return foreign is null ? null : $"An image to remove must be a repository of {project}, not '{foreign}'.";
        }
    }
}
