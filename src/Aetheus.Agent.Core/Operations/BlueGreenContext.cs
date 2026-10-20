// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// The environment a blue-green operation acts on, parsed once and validated before anything is
/// touched. Every blue-green step re-derives this from its own task environment rather than
/// inheriting it, because the steps run as separate pipeline tasks and must not depend on state left
/// in a predecessor's process.
///
/// The validation here is the real boundary: the state directory must be absolute, the ports must be
/// four distinct numbers, and the Compose files must exist. A task that cannot describe its own
/// environment coherently is refused before it can start or stop a container.
/// </summary>
internal sealed record BlueGreenContext
{
    internal required string Project { get; init; }
    internal required string StateDir { get; init; }
    internal required string EnvFile { get; init; }
    internal required IReadOnlyList<string> ComposeFiles { get; init; }
    internal required int FrontBlue { get; init; }
    internal required int BackBlue { get; init; }
    internal required int FrontGreen { get; init; }
    internal required int BackGreen { get; init; }

    /// <summary>Container name Compose gives the shared database, as <c>APPNAME-ENV-database</c>.</summary>
    internal required string DatabaseContainer { get; init; }
    internal required string DatabaseUser { get; init; }
    internal required string DatabaseName { get; init; }

    /// <summary>
    /// Variables the Compose files interpolate for this run - image tags, application version, source
    /// revision, a run-scoped smoke identity. A shell cutover exported them into the process it then
    /// ran Compose from; a typed step has no such process, and the agent service environment does not
    /// contain them, so Compose would substitute its own defaults and start a placeholder image while
    /// reporting a successful cutover. The step lists what it needs and only that list arrives here.
    /// </summary>
    internal required IReadOnlyDictionary<string, string> ComposeEnvironment { get; init; }

    /// <summary>Prefix the control plane uses to mark a task variable as a Compose input.</summary>
    private const string ComposeEnvPrefix = "AETHEUS_BG_COMPOSE_ENV_";

    internal string ColorFile => Path.Combine(StateDir, "live-color");
    internal string JournalDir => JournalDirectoryOf(StateDir);

    internal static string JournalDirectoryOf(string stateDir) => Path.Combine(stateDir, "deployment-transaction");
    internal string SourceCommitFile => Path.Combine(StateDir, "source-commit");

    internal (int Front, int Back) PortsFor(string colour) => colour switch
    {
        "blue" => (FrontBlue, BackBlue),
        "green" => (FrontGreen, BackGreen),
        _ => throw new ArgumentOutOfRangeException(nameof(colour), colour, "Unknown blue-green colour.")
    };

    internal static string Opposite(string colour) => colour == "blue" ? "green" : "blue";

    /// <summary>
    /// Builds the context from the task environment, or returns the reason it is unusable. Returning
    /// a reason rather than throwing keeps the caller able to report an honest step failure.
    /// </summary>
    internal static bool TryCreate(
        string project, IReadOnlyDictionary<string, string> env, out BlueGreenContext? context, out string error)
    {
        context = null;
        error = string.Empty;

        var stateDir = env.GetValueOrDefault("AETHEUS_BG_STATE_DIR", string.Empty).Trim();
        var envFile = env.GetValueOrDefault("AETHEUS_BG_ENV_FILE", string.Empty).Trim();
        if (!TryStateDirectory(stateDir, out error)
            || !TryComposeInputs(envFile, env, out var composeFiles, out error)
            || !TryPorts(env, out var ports, out error)
            || !TryDatabaseIdentity(envFile, out var database, out error))
        {
            return false;
        }

        context = new BlueGreenContext
        {
            Project = project,
            StateDir = stateDir,
            EnvFile = envFile,
            ComposeFiles = composeFiles,
            FrontBlue = ports[0],
            BackBlue = ports[1],
            FrontGreen = ports[2],
            BackGreen = ports[3],
            DatabaseContainer = database.Container,
            DatabaseUser = database.User,
            DatabaseName = database.Name,
            ComposeEnvironment = ReadComposeEnvironment(env)
        };
        return true;
    }

    /// <summary>Rebuild only state-directory and port context for watchdog rollback without relying
    /// on retained workspace or Compose files. Upstream restoration and journal operations work
    /// with this context; Compose operations fail explicitly.</summary>
    internal static bool TryCreateForRecovery(
        string project, IReadOnlyDictionary<string, string> env, out BlueGreenContext? context, out string error)
    {
        if (TryCreate(project, env, out context, out error)) return true;
        context = null;
        var stateDir = env.GetValueOrDefault("AETHEUS_BG_STATE_DIR", string.Empty).Trim();
        if (!TryStateDirectory(stateDir, out error) || !TryPorts(env, out var ports, out error)) return false;
        context = new BlueGreenContext
        {
            Project = project,
            StateDir = stateDir,
            EnvFile = env.GetValueOrDefault("AETHEUS_BG_ENV_FILE", string.Empty).Trim(),
            ComposeFiles = [],
            FrontBlue = ports[0],
            BackBlue = ports[1],
            FrontGreen = ports[2],
            BackGreen = ports[3],
            DatabaseContainer = string.Empty,
            DatabaseUser = string.Empty,
            DatabaseName = string.Empty,
            ComposeEnvironment = ReadComposeEnvironment(env)
        };
        return true;
    }

    /// <summary>
    /// Collects the prefixed task variables and strips the prefix. Only these reach the Compose child
    /// process: the task environment also carries vault secrets and orchestration values that have no
    /// business being visible to the containers Compose starts.
    /// </summary>
    private static Dictionary<string, string> ReadComposeEnvironment(IReadOnlyDictionary<string, string> env)
    {
        var composeEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in env)
        {
            if (!key.StartsWith(ComposeEnvPrefix, StringComparison.Ordinal)) continue;
            var name = key[ComposeEnvPrefix.Length..];
            if (name.Length > 0) composeEnvironment[name] = value;
        }
        return composeEnvironment;
    }

    /// <summary>
    /// A first deployment has no state directory yet, so it is created rather than refused. When the
    /// parent is not writable by the agent the failure is reported as the provisioning problem it is,
    /// instead of a misleading "does not exist".
    /// </summary>
    private static bool TryStateDirectory(string stateDir, out string error)
    {
        error = string.Empty;
        if (stateDir.Length == 0 || !Path.IsPathRooted(stateDir) || stateDir.Contains("..", StringComparison.Ordinal))
        {
            error = $"AETHEUS_BG_STATE_DIR must be an absolute path without traversal, got '{stateDir}'.";
            return false;
        }
        if (Directory.Exists(stateDir)) return true;
        try
        {
            Directory.CreateDirectory(stateDir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stateDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"State directory {stateDir} does not exist and could not be created ({ex.Message}). "
                + "Provision it for the agent user before deploying.";
            return false;
        }
    }

    private static bool TryComposeInputs(
        string envFile, IReadOnlyDictionary<string, string> env, out List<string> composeFiles, out string error)
    {
        composeFiles = [];
        if (envFile.Length == 0 || !File.Exists(envFile))
        {
            error = $"Environment file is missing: '{envFile}'";
            return false;
        }
        composeFiles = env.GetValueOrDefault("AETHEUS_BG_COMPOSE_FILES", string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (composeFiles.Count == 0)
        {
            error = "AETHEUS_BG_COMPOSE_FILES is required.";
            return false;
        }
        var missing = composeFiles.FirstOrDefault(file => !File.Exists(file));
        if (missing is not null)
        {
            error = $"Compose file is missing: {missing}";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool TryPorts(IReadOnlyDictionary<string, string> env, out int[] ports, out string error)
    {
        ports = [];
        var keys = new[]
        {
            "AETHEUS_BG_PORT_FRONT_BLUE", "AETHEUS_BG_PORT_BACK_BLUE",
            "AETHEUS_BG_PORT_FRONT_GREEN", "AETHEUS_BG_PORT_BACK_GREEN"
        };
        var parsed = new int[keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            if (!TryPort(env, keys[index], out parsed[index], out error)) return false;
        }
        if (parsed.Distinct().Count() != parsed.Length)
        {
            error = "The four blue-green ports must be distinct.";
            return false;
        }
        ports = parsed;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Reads the database identity from the same env file Compose interpolates, so the container this
    /// talks to is by construction the one Compose started.
    /// </summary>
    private static bool TryDatabaseIdentity(
        string envFile, out (string Container, string User, string Name) database, out string error)
    {
        database = default;
        var settings = ReadEnvFile(envFile);
        var appName = settings.GetValueOrDefault("APPNAME", string.Empty);
        var environment = settings.GetValueOrDefault("ENV", string.Empty);
        var dbUser = settings.GetValueOrDefault("DB_USER", string.Empty);
        if (appName.Length == 0 || environment.Length == 0 || dbUser.Length == 0)
        {
            error = $"APPNAME, ENV and DB_USER must all be set in {envFile}.";
            return false;
        }
        // Compose defaults DB_NAME to APPNAME, so mirror that rather than inventing a second rule.
        var dbName = settings.GetValueOrDefault("DB_NAME", string.Empty) is { Length: > 0 } name ? name : appName;
        database = ($"{appName}-{environment}-database", dbUser, dbName);
        error = string.Empty;
        return true;
    }

    private static Dictionary<string, string> ReadEnvFile(string path)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            // Last assignment wins, matching how Compose reads the same file.
            settings[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return settings;
    }

    private static bool TryPort(
        IReadOnlyDictionary<string, string> env, string key, out int port, out string error)
    {
        error = string.Empty;
        var raw = env.GetValueOrDefault(key, string.Empty).Trim();
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535)
        {
            return true;
        }
        error = $"{key} must be a valid port, got '{raw}'.";
        return false;
    }

    /// <summary>Compose argv shared by every operation: fixed project, fixed env file, fixed files.</summary>
    internal List<string> ComposeArgs()
    {
        var args = new List<string> { "compose", "-p", Project, "--env-file", EnvFile };
        foreach (var file in ComposeFiles) args.AddRange(["-f", file]);
        return args;
    }
}
