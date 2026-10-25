// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Turns a <c>bluegreen-*</c> step definition into the validated task environment the agent expects.
///
/// This lives apart from the dispatcher because the checks are the interesting part: the four
/// operations act on a live environment, so a definition that is merely plausible is not enough.
/// Every value is validated here, before a task is ever created, and a rejection carries the reason
/// so the step fails honestly instead of the agent discovering the problem mid-cutover.
/// </summary>
internal sealed record BlueGreenStepBinding
{
    internal required string Project { get; init; }
    internal required Dictionary<string, string> Variables { get; init; }

    /// <summary><c>compose_env</c> names this step did not forward because the run does not define
    /// them at this point, reported so a skip is never silent. Always empty for the steps that start
    /// a container (<c>bluegreen-up</c>, <c>bluegreen-migrate</c>), which refuse instead.</summary>
    internal IReadOnlyList<string> SkippedComposeEnv { get; init; } = [];

    /// <summary>
    /// The run-scoped bootstrap identity. The control plane derives it for <c>bluegreen-up</c> alone:
    /// it must be in the container environment at start-up, and every later step acts on a colour
    /// that already carries it. Any other step drops these names from <c>compose_env</c> by design,
    /// so one list can serve every step of a deployment.
    /// </summary>
    /// The telemetry and web analytics names follow the same rule: <c>bluegreen-up</c> alone receives
    /// them (<c>AppDeployEnvProvider</c>), so one v5 list naming them made the strict migrate step
    /// refuse a name it was never meant to receive.
    internal static readonly string[] StartOnlyNames =
    [
        "DEPLOYMENT_BOOTSTRAP_USER", "DEPLOYMENT_BOOTSTRAP_PASSWORD", "BOOTSTRAP_STAMP", "DEPLOYMENT_BOOTSTRAP_EXPIRES_AT_UTC",
        .. Aetheus.Back.Components.AppMonitoring.AppDeployEnvProvider.DerivedNames
    ];

    internal static bool TryBind(
        OperationKind operation,
        PipelineStepDefinition stepDef,
        Func<string?, string> substitute,
        IReadOnlyDictionary<string, string> baseVariables,
        out BlueGreenStepBinding? binding,
        out string error)
    {
        binding = null;

        var project = substitute(stepDef.Project);
        if (!OperationTargetValidator.IsValid(operation, project))
        {
            error = $"invalid or missing 'project' '{project}'.";
            return false;
        }

        if (!TryStateDir(substitute(stepDef.StateDir), out var stateDir, out error)
            || !TryPorts(substitute(stepDef.Ports), out var ports, out error))
        {
            return false;
        }

        var composeFiles = substitute(stepDef.ComposeFiles);
        var envFile = substitute(stepDef.EnvFile);
        if (composeFiles.Length == 0 || envFile.Length == 0)
        {
            error = "'compose_files' and 'env_file' are required.";
            return false;
        }

        var reloadHelper = substitute(stepDef.ReloadHelper);
        var revision = substitute(stepDef.Revision);
        var upstreamTemplate = substitute(stepDef.UpstreamTemplate);
        var upstreamConf = substitute(stepDef.UpstreamConf);
        var migrationsDir = substitute(stepDef.MigrationsDir);

        if (!TryOperationRequirements(
                operation, reloadHelper, revision, upstreamTemplate, upstreamConf, migrationsDir, out error))
        {
            return false;
        }

        if (!TryComposeEnv(operation, substitute(stepDef.ComposeEnv), baseVariables, out var composeEnv, out var skipped, out error))
            return false;
        if (!TryConfirmMinutes(operation, substitute(stepDef.ConfirmMinutes), out var confirmMinutes, out error))
            return false;

        binding = new BlueGreenStepBinding
        {
            Project = project,
            SkippedComposeEnv = skipped,
            Variables = new Dictionary<string, string>(baseVariables, StringComparer.OrdinalIgnoreCase)
            {
                ["AETHEUS_BG_STATE_DIR"] = stateDir,
                ["AETHEUS_BG_ENV_FILE"] = envFile,
                ["AETHEUS_BG_COMPOSE_FILES"] = composeFiles,
                ["AETHEUS_BG_PORT_FRONT_BLUE"] = ports[0],
                ["AETHEUS_BG_PORT_BACK_BLUE"] = ports[1],
                ["AETHEUS_BG_PORT_FRONT_GREEN"] = ports[2],
                ["AETHEUS_BG_PORT_BACK_GREEN"] = ports[3],
                ["AETHEUS_BG_RELOAD_HELPER"] = reloadHelper,
                ["AETHEUS_BG_REVISION"] = revision,
                ["AETHEUS_BG_UPSTREAM_TEMPLATE"] = upstreamTemplate,
                ["AETHEUS_BG_UPSTREAM_CONF"] = upstreamConf,
                ["AETHEUS_BG_MIGRATIONS_DIR"] = migrationsDir
            }
        };
        if (confirmMinutes > 0)
            binding.Variables["AETHEUS_BG_CONFIRM_MINUTES"] = confirmMinutes.ToString(CultureInfo.InvariantCulture);
        foreach (var (name, value) in composeEnv)
            binding.Variables[ComposeEnvPrefix + name] = value;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Prefix carrying the Compose interpolation variables to the agent. The values already travel in
    /// the task environment, but only the names listed under this prefix reach the
    /// <c>docker compose</c> child process, so the forwarded set stays explicit and auditable instead
    /// of being "whatever the run happened to define".
    /// </summary>
    internal const string ComposeEnvPrefix = "AETHEUS_BG_COMPOSE_ENV_";

    /// <summary>Only a switch arms a bounded host confirmation window; empty or zero means
    /// none.</summary>
    private static bool TryConfirmMinutes(OperationKind operation, string raw, out int minutes, out string error)
    {
        minutes = 0;
        error = string.Empty;
        if (raw.Length == 0 || raw == "0") return true;
        if (operation != OperationKind.BlueGreenSwitch)
        {
            error = "'confirm_minutes' only applies to bluegreen-switch.";
            return false;
        }
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes is < 1 or > 240)
        {
            error = $"'confirm_minutes' must be a whole number of minutes between 1 and 240, got '{raw}'.";
            return false;
        }
        return true;
    }

    /// <summary>Bounds the forwarded set; a definition needing more is describing something else.</summary>
    private const int MaxComposeEnvEntries = 32;

    /// <summary>
    /// Resolves <c>compose_env</c> against the run variables, per operation (D-01).
    /// <para>
    /// A step that starts a container - <c>bluegreen-up</c> the colour, <c>bluegreen-migrate</c> the
    /// migration job - refuses an unresolvable name rather than dropping it: Compose substitutes its
    /// own default for a variable it cannot see, so a missing image tag would start the placeholder
    /// image instead of the one CI built, and the step would report success on the wrong thing. The
    /// only exception is the migrate step and <see cref="StartOnlyNames"/>, which it never receives.
    /// </para>
    /// <para>
    /// Switch, commit, rollback and retire act on containers that already exist. They forward what
    /// resolves and skip the rest, reported through <see cref="SkippedComposeEnv"/>: a rollback fires
    /// on <c>failed()</c>, including a failure before the image tags were ever published, and refusing
    /// it there turned the one compensation into a second failure.
    /// </para>
    /// </summary>
    private static bool TryComposeEnv(
        OperationKind operation,
        string raw,
        IReadOnlyDictionary<string, string> baseVariables,
        out List<KeyValuePair<string, string>> composeEnv,
        out List<string> skipped,
        out string error)
    {
        composeEnv = [];
        skipped = [];
        error = string.Empty;
        var names = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return true;
        if (names.Length > MaxComposeEnvEntries)
        {
            error = $"'compose_env' lists {names.Length} variables, more than the {MaxComposeEnvEntries} allowed.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!IsEnvironmentName(name))
            {
                error = $"'compose_env' entry '{name}' is not a valid environment variable name.";
                return false;
            }
            if (!seen.Add(name))
            {
                error = $"'compose_env' lists '{name}' twice.";
                return false;
            }
            // An explicitly empty library value is still defined and must be forwarded to Compose.
            if (baseVariables.TryGetValue(name, out var value))
            {
                composeEnv.Add(new KeyValuePair<string, string>(name, value));
                continue;
            }
            if (operation != OperationKind.BlueGreenUp && StartOnlyNames.Contains(name, StringComparer.Ordinal))
                continue;
            if (operation is OperationKind.BlueGreenUp or OperationKind.BlueGreenMigrate)
            {
                error = $"'compose_env' names '{name}', which this run does not define; "
                    + "Compose would silently fall back to its own default.";
                return false;
            }
            skipped.Add(name);
        }
        return true;
    }

    private static bool IsEnvironmentName(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool TryStateDir(string stateDir, out string validated, out string error)
    {
        validated = stateDir;
        if (stateDir.Length == 0 || !stateDir.StartsWith('/') || stateDir.Contains("..", StringComparison.Ordinal))
        {
            error = $"'state_dir' must be an absolute path without traversal, got '{stateDir}'.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool TryPorts(string raw, out string[] ports, out string error)
    {
        ports = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ports.Length != 4 || !ports.All(IsPort))
        {
            error = "'ports' must list four valid ports as frontBlue,backBlue,frontGreen,backGreen.";
            return false;
        }
        if (ports.Distinct(StringComparer.Ordinal).Count() != 4)
        {
            error = "'ports' must be four distinct values.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool IsPort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535;

    /// <summary>
    /// What each blue-green operation requires of its own fields. Extracted from <c>TryBind</c> so the
    /// binder keeps one job (shape the binding) and this keeps the other (say what an operation needs),
    /// which is also what keeps <c>TryBind</c> under the repository's complexity budget.
    /// </summary>
    private static bool TryOperationRequirements(
        OperationKind operation,
        string reloadHelper,
        string revision,
        string upstreamTemplate,
        string upstreamConf,
        string migrationsDir,
        out string error)
    {
        error = string.Empty;
        switch (operation)
        {
            case OperationKind.BlueGreenSwitch:
                return TrySwitchFields(reloadHelper, revision, upstreamTemplate, upstreamConf, out error);

            // A step that carries a revision (the template's first step does since catalog v2) must not
            // reach the schema with a placeholder, or the refusal would come only at the switch, after the
            // shared schema was already migrated.
            case OperationKind.BlueGreenMigrate when HasUnresolvedReference(revision):
                error = UnresolvedRevisionMessage(revision);
                return false;

            // Without the migration sources the expand/contract gate cannot inspect what is about to run
            // against a schema the previous colour is still serving, so refuse rather than skip it.
            case OperationKind.BlueGreenMigrate when migrationsDir.Length == 0:
                error = "'migrations_dir' is required so the expand/contract gate can inspect pending migrations.";
                return false;

            case OperationKind.BlueGreenRollback or OperationKind.BlueGreenRevert
                when upstreamConf.Length == 0 || reloadHelper.Length == 0:
                error = "'upstream_conf' and 'reload_helper' are required to restore traffic.";
                return false;

            // rollback and retire hand reload_helper to `sudo -n` exactly like switch does, so they get
            // the same shape check. The real boundary is argv execution plus an argv-exact sudoers rule,
            // but a validation present on one of three paths reads as an oversight, not defense in depth.
            case OperationKind.BlueGreenRollback or OperationKind.BlueGreenRetire or OperationKind.BlueGreenRevert
                when reloadHelper.Length > 0:
                return TryReloadHelperShape(reloadHelper, out error);

            default:
                return true;
        }
    }

    /// <summary>
    /// The shape every privileged reload helper must have, wherever it is used: an absolute path, no
    /// traversal, no whitespace. Whitespace matters because the value reaches a `sudo -n` grant, and a
    /// space is what would turn one granted command into two.
    /// </summary>
    private static bool TryReloadHelperShape(string reloadHelper, out string error)
    {
        if (!reloadHelper.StartsWith('/')
            || reloadHelper.Contains("..", StringComparison.Ordinal)
            || reloadHelper.Any(char.IsWhiteSpace))
        {
            error = $"'reload_helper' must be an absolute path with no whitespace, got '{reloadHelper}'.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// A <c>$(NAME)</c> the run could not resolve stays literal in the substituted text, so a revision
    /// derived from a run variable (the template's default is the launch commit) that the run never
    /// defined would otherwise be written to the journal as that placeholder.
    /// </summary>
    private static bool HasUnresolvedReference(string revision) =>
        revision.Contains("$(", StringComparison.Ordinal);

    private static string UnresolvedRevisionMessage(string revision) =>
        $"'revision' is '{revision}', an unresolved variable reference: the run defines no source revision for the journal. "
        + "Launch the pipeline on a commit or set BG_REVISION to the revision being deployed.";

    /// <summary>
    /// The reload helper is invoked argv-exact through sudo, so it must be a fixed absolute path.
    /// Allowing whitespace or traversal would let a variable turn the grant into an arbitrary
    /// privileged command.
    /// </summary>
    private static bool TrySwitchFields(
        string reloadHelper, string revision, string upstreamTemplate, string upstreamConf, out string error)
    {
        if (!TryReloadHelperShape(reloadHelper, out error)) return false;
        if (revision.Length == 0)
        {
            error = "'revision' is required so the journal records what was deployed.";
            return false;
        }
        if (HasUnresolvedReference(revision))
        {
            error = UnresolvedRevisionMessage(revision);
            return false;
        }
        // Rendering the template is the switch. Allowing the step to omit it would produce a reload
        // of the configuration already in place, reported as a successful cutover.
        if (upstreamTemplate.Length == 0 || upstreamConf.Length == 0)
        {
            error = "'upstream_template' and 'upstream_conf' are required: without them the switch would reload the live configuration unchanged.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Maps a step type to its operation, or <see cref="OperationKind.None"/> when the type is not one
    /// of the five. A catch-all default would turn a typo such as <c>bluegreen-swtich</c> into a
    /// commit against a live environment, so an unrecognised type is refused rather than guessed.
    /// </summary>
    internal static OperationKind OperationFor(string type) => type switch
    {
        "bluegreen-migrate" => OperationKind.BlueGreenMigrate,
        "bluegreen-up" => OperationKind.BlueGreenUp,
        "bluegreen-switch" => OperationKind.BlueGreenSwitch,
        "bluegreen-commit" => OperationKind.BlueGreenCommit,
        "bluegreen-rollback" => OperationKind.BlueGreenRollback,
        "bluegreen-retire" => OperationKind.BlueGreenRetire,
        "bluegreen-revert" => OperationKind.BlueGreenRevert,
        _ => OperationKind.None
    };
}
