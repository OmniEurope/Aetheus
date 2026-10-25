// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Shell command construction for pipeline steps and system tasks (clone / prepare / cleanup).
/// Repo URL / branch / credentials are passed via environment variables so user-controlled values
/// cannot break out of the shell template (defense in depth against command injection).
/// Git auth uses an ephemeral <c>http.extraHeader</c> injected through <c>GIT_CONFIG_*</c>
/// environment variables (F-013): credentials never appear in the remote URL, are never persisted
/// to the workspace <c>.git/config</c>, and never show up in the process argument list.
/// </summary>
internal static partial class PipelineCommandBuilder
{
    // F-033: single home for the magic workspace paths and system-task timeouts.
    internal const int PrepareTimeoutSeconds = 120;
    internal const int CleanupTimeoutSeconds = 60;
    internal const int ArtifactCollectionTimeoutSeconds = 300;

    /// <summary>
    /// Linux agents run under systemd with <c>PrivateTmp=true</c>, which hands the service a fresh
    /// tmpfs on every start. A workspace under /tmp therefore did not survive an agent restart, and
    /// a self-update landing between two jobs of a live run wiped the sources out from under the
    /// next job: the step preamble recreated the empty directory and the run failed on the first
    /// missing repository file (production run 2185). The workspace now lives under the agent's
    /// durable work directory - the Linux installer WORK_DIR and the agent default - which is listed
    /// in the unit ReadWritePaths so ProtectSystem=strict still allows writes, and is where
    /// container workspaces already live. Windows was never affected: C:\w is durable.
    /// The slot stays an opaque hash of the run id (Azure-style short path) - neither the app name
    /// nor the sequential run id leaks into logs or <c>pwd</c>.
    /// </summary>
    internal const string LinuxAgentWorkDirectory = "/var/lib/aetheus-agent";

    /// <summary>
    /// Emitted by a step preamble that had to recreate its own run workspace. Phrased for whoever
    /// reads the failing log: it names the cause instead of leaving them to infer it from whatever
    /// missing file the step reports next.
    /// </summary>
    internal const string WorkspaceLostWarning =
        "aetheus: run workspace was missing and has been recreated empty - the agent restarted "
        + "during this run (self-update, service restart or reboot). Steps needing the repository "
        + "will report missing files; re-run the pipeline to rebuild the workspace.";

    internal static string GetDefaultWorkspace(int runId, bool isWindows)
    {
        var slot = GetWorkspaceSlot(runId);
        return isWindows ? $@"C:\w\{slot}\s" : $"{LinuxAgentWorkDirectory}/w/{slot}/s";
    }

    internal static string GetWorkspaceSlot(int runId)
    {
        var mixed = (uint)runId * 2654435761u;
        return mixed.ToString("x8");
    }

    // Qualified step outputs use the Azure-style $(Stage.Step.Variable) syntax. Keep the
    // character set deliberately narrow so ordinary shell command substitutions are untouched.
    // `$(NAME:-default)` carries a fallback used when NAME is missing or empty, as in POSIX. The
    // default cannot contain a parenthesis: nesting is not supported, and refusing it keeps
    // `$(A:-$(B))` from matching half of itself.
    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_.]*)(?::-([^()]*))?\)")]
    private static partial Regex VariablePattern();

    /// <summary>Every default applies: the value is being rendered for the step that runs now.</summary>
    private static readonly Func<string, bool> EveryDefaultApplies = _ => true;

    // S-TECH-V6QN: ${{ parameters.X }} is a distinct namespace from $(X). It resolves only from the
    // run-parameter keys (carried as "parameters.X" in the variable map), so a same-named YAML
    // variable can never shadow a run parameter - matching Azure DevOps' compile-time-vs-runtime split.
    [GeneratedRegex(@"\$\{\{\s*parameters\.([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex ParameterPattern();

    /// <summary>Prefix under which run parameters are stored in the variable map for the
    /// <c>${{ parameters.X }}</c> namespace. Dotted, so it is never a valid shell env name and is
    /// stripped from the exported step environment (see <c>ScopeStepEnvironment</c>).</summary>
    internal const string ParameterKeyPrefix = "parameters.";

    internal static string Substitute(string command, IReadOnlyDictionary<string, string> variables)
        => Substitute(command, variables, EveryDefaultApplies);

    /// <summary>The names <paramref name="text"/> references as <c>$(NAME)</c> or <c>$(NAME:-default)</c>,
    /// read with the same pattern <see cref="Substitute(string, IReadOnlyDictionary{string, string})"/> uses.</summary>
    internal static IEnumerable<string> ReferencedVariableNames(string text) =>
        string.IsNullOrEmpty(text)
            ? []
            : VariablePattern().Matches(text).Select(match => match.Groups[1].Value);

    /// <summary>The parameter names <paramref name="text"/> references as <c>${{ parameters.NAME }}</c>.</summary>
    internal static IEnumerable<string> ReferencedParameterNames(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : ParameterPattern().Matches(text).Select(match => match.Groups[1].Value);

    /// <summary>
    /// Substitutes <c>${{ parameters.X }}</c> and <c>$(X)</c>. An unknown <c>$(X)</c> stays literal.
    /// <c>$(X:-default)</c> takes the value of X when it is defined and not empty; otherwise
    /// <paramref name="defaultApplies"/> decides whether the default is used now or the reference
    /// is kept literal because X may still be provided later in the run. A null predicate is the
    /// pre-default behaviour, which leaves every reference carrying a default untouched: vault
    /// secrets are opaque data, and a value that happens to contain that shape must not change.
    /// </summary>
    internal static string Substitute(
        string command, IReadOnlyDictionary<string, string> variables, Func<string, bool>? defaultApplies)
    {
        if (string.IsNullOrEmpty(command))
            return command;

        // Resolve the ${{ parameters.X }} namespace first, then the $(X) variable syntax.
        command = ParameterPattern().Replace(command, match =>
        {
            var paramName = match.Groups[1].Value;
            return variables.TryGetValue(ParameterKeyPrefix + paramName, out var pv) ? pv : match.Value;
        });

        return VariablePattern().Replace(command, match =>
        {
            var varName = match.Groups[1].Value;
            var fallback = match.Groups[2];
            if (!fallback.Success)
                return variables.TryGetValue(varName, out var value) ? value : match.Value;
            if (defaultApplies is null)
                return match.Value;
            if (variables.TryGetValue(varName, out var defined) && defined.Length > 0)
                return defined;
            return defaultApplies(varName) ? fallback.Value : match.Value;
        });
    }

    // --- Git auth preambles (F-013) ---

    private const string LinuxGitAuthPreamble =
        "if [ -n \"${GIT_USERNAME:-}\" ] && [ -n \"${GIT_PASSWORD:-}\" ]; then\n" +
        "  export GIT_CONFIG_COUNT=1\n" +
        "  export GIT_CONFIG_KEY_0=\"http.extraHeader\"\n" +
        "  export GIT_CONFIG_VALUE_0=\"Authorization: Basic $(printf '%s:%s' \"${GIT_USERNAME}\" \"${GIT_PASSWORD}\" | base64 | tr -d '\\n')\"\n" +
        "fi\n";

    private const string WindowsGitAuthPreamble =
        "if ($env:GIT_USERNAME -and $env:GIT_PASSWORD) {\n" +
        "  $__pair = \"$($env:GIT_USERNAME):$($env:GIT_PASSWORD)\"\n" +
        "  $env:GIT_CONFIG_COUNT = '1'\n" +
        "  $env:GIT_CONFIG_KEY_0 = 'http.extraHeader'\n" +
        "  $env:GIT_CONFIG_VALUE_0 = 'Authorization: Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($__pair))\n" +
        "}\n";

    // --- Clone scripts (F-016: single builder, parameterized by the working-dir expression) ---

    private static string BuildLinuxCloneScript(string workdirExpr) =>
        "set -e\n" +
        "export GIT_TERMINAL_PROMPT=0\n" +
        LinuxGitAuthPreamble +
        $"mkdir -p \"{workdirExpr}\"\n" +
        $"chmod 700 \"{workdirExpr}\"\n" +
        $"cd \"{workdirExpr}\"\n" +
        "GIT_HISTORY_DEPTH=\"${AETHEUS_GIT_HISTORY_DEPTH:-1}\"\n" +
        "case \"$GIT_HISTORY_DEPTH\" in *[!0-9]*|'') echo \"AETHEUS_GIT_HISTORY_DEPTH must be a positive integer.\" >&2; exit 1 ;; esac\n" +
        "[ \"$GIT_HISTORY_DEPTH\" -ge 1 ] || { echo \"AETHEUS_GIT_HISTORY_DEPTH must be at least 1.\" >&2; exit 1; }\n" +
        "if [ -n \"${BUILD_SOURCEVERSION:-}\" ]; then\n" +
        "  if [ ! -d .git ]; then git init; git remote add origin \"${REPOSITORY_URL}\"; fi\n" +
        "  git fetch --depth \"$GIT_HISTORY_DEPTH\" origin \"${DEFAULT_BRANCH:-main}\"\n" +
        "  if ! git cat-file -e \"${BUILD_SOURCEVERSION}^{commit}\" 2>/dev/null; then\n" +
        "    git fetch --depth 1 origin \"${BUILD_SOURCEVERSION}\" || true\n" +
        "  fi\n" +
        "  if ! git cat-file -e \"${BUILD_SOURCEVERSION}^{commit}\" 2>/dev/null; then\n" +
        "    git fetch --depth 1 origin '+refs/heads/*:refs/remotes/origin/*'\n" +
        "  fi\n" +
        "  if ! git cat-file -e \"${BUILD_SOURCEVERSION}^{commit}\" 2>/dev/null; then\n" +
        "    git fetch --deepen 1000 origin '+refs/heads/*:refs/remotes/origin/*'\n" +
        "  fi\n" +
        "  git cat-file -e \"${BUILD_SOURCEVERSION}^{commit}\"\n" +
        "  git checkout --detach \"${BUILD_SOURCEVERSION}\"\n" +
        "  git reset --hard \"${BUILD_SOURCEVERSION}\"\n" +
        "  echo \"Repository pinned to ${BUILD_SOURCEVERSION}\"\n" +
        "elif [ -d .git ]; then\n" +
        "  git fetch --depth 1 origin \"${DEFAULT_BRANCH:-main}\"\n" +
        "  git reset --hard \"origin/${DEFAULT_BRANCH:-main}\"\n" +
        "  echo \"Repository updated (fetch + reset)\"\n" +
        "else\n" +
        "  git clone --branch \"${DEFAULT_BRANCH:-main}\" --single-branch --depth 1 \"${REPOSITORY_URL}\" .\n" +
        "  echo \"Repository cloned\"\n" +
        "fi\n";

    private static string BuildWindowsCloneScript(string workdirExpr) =>
        "$ErrorActionPreference = 'Stop'\n" +
        "$env:GIT_TERMINAL_PROMPT = '0'\n" +
        WindowsGitAuthPreamble +
        // BACK-017: PowerShell does not stop on a native exit code, even with ErrorActionPreference
        // Stop. Every mandatory git call checks its own, so a failed checkout never reads as prepared;
        // the fallback fetches stay optional, the final cat-file check decides.
        "function Assert-GitSucceeded([string]$operation) { if ($LASTEXITCODE -ne 0) { throw \"git $operation failed with exit code $LASTEXITCODE.\" } }\n" +
        $"New-Item -ItemType Directory -Force -Path '{workdirExpr}' | Out-Null\n" +
        $"Set-Location '{workdirExpr}'\n" +
        "$branch = if ($env:DEFAULT_BRANCH) { $env:DEFAULT_BRANCH } else { 'main' }\n" +
        "$historyDepth = if ($env:AETHEUS_GIT_HISTORY_DEPTH) { $env:AETHEUS_GIT_HISTORY_DEPTH } else { '1' }\n" +
        "if ($historyDepth -notmatch '^[1-9][0-9]*$') { throw 'AETHEUS_GIT_HISTORY_DEPTH must be a positive integer.' }\n" +
        "if ($env:BUILD_SOURCEVERSION) {\n" +
        "  if (-not (Test-Path '.git')) { git init; Assert-GitSucceeded 'init'; git remote add origin $env:REPOSITORY_URL; Assert-GitSucceeded 'remote add' }\n" +
        "  git fetch --depth $historyDepth origin $branch; Assert-GitSucceeded 'fetch'\n" +
        "  git cat-file -e \"$env:BUILD_SOURCEVERSION`^{commit}\" 2>$null\n" +
        "  if ($LASTEXITCODE -ne 0) { git fetch --depth 1 origin $env:BUILD_SOURCEVERSION }\n" +
        "  git cat-file -e \"$env:BUILD_SOURCEVERSION`^{commit}\" 2>$null\n" +
        "  if ($LASTEXITCODE -ne 0) { git fetch --depth 1 origin '+refs/heads/*:refs/remotes/origin/*' }\n" +
        "  git cat-file -e \"$env:BUILD_SOURCEVERSION`^{commit}\" 2>$null\n" +
        "  if ($LASTEXITCODE -ne 0) { git fetch --deepen 1000 origin '+refs/heads/*:refs/remotes/origin/*' }\n" +
        "  git cat-file -e \"$env:BUILD_SOURCEVERSION`^{commit}\"\n" +
        "  if ($LASTEXITCODE -ne 0) { throw 'Pinned source commit is not reachable from the configured branch.' }\n" +
        "  git checkout --detach $env:BUILD_SOURCEVERSION; Assert-GitSucceeded 'checkout'\n" +
        "  git reset --hard $env:BUILD_SOURCEVERSION; Assert-GitSucceeded 'reset'\n" +
        "  Write-Output \"Repository pinned to $env:BUILD_SOURCEVERSION\"\n" +
        "} elseif (Test-Path '.git') {\n" +
        "  git fetch --depth 1 origin $branch; Assert-GitSucceeded 'fetch'\n" +
        "  git reset --hard \"origin/$branch\"; Assert-GitSucceeded 'reset'\n" +
        "  Write-Output 'Repository updated (fetch + reset)'\n" +
        "} else {\n" +
        "  git clone --branch $branch --single-branch --depth 1 $env:REPOSITORY_URL .; Assert-GitSucceeded 'clone'\n" +
        "  Write-Output 'Repository cloned'\n" +
        "}\n";

    internal static string BuildLinuxCloneCommand(string workspace) =>
        BuildLinuxCloneScript(workspace) +
        "echo \"Workspace ready: ${PWD}\"\n" +
        "ls -la";

    internal static string BuildWindowsCloneCommand(string workspace) =>
        BuildWindowsCloneScript(workspace) +
        "Write-Output \"Workspace ready: $(Get-Location)\"\n" +
        "Get-ChildItem | Format-Table Name, Length, LastWriteTime -AutoSize";

    internal static string BuildLinuxPrepareCommand(string workspace) =>
        "set -e\n" +
        $"mkdir -p \"{workspace}\"\n" +
        $"chmod 700 \"{workspace}\"\n" +
        $"echo \"Workspace prepared: {workspace}\"\n" +
        $"ls -la \"{workspace}\"";

    internal static string BuildWindowsPrepareCommand(string workspace) =>
        "$ErrorActionPreference = 'Stop'\n" +
        $"New-Item -ItemType Directory -Force -Path '{workspace}' | Out-Null\n" +
        $"Write-Output 'Workspace prepared: {workspace}'\n" +
        $"Get-ChildItem '{workspace}' | Format-Table Name, Length -AutoSize";

    // BACK-017 (decision of 2026-10-08): a cleanup that cannot remove the workspace fails, after three
    // attempts spaced out for a process that still holds a file; it never prints a success it did not
    // achieve. Only the cleanup step fails, the business steps keep their own status.
    internal static string BuildLinuxCleanupCommand(string workspace) =>
        $"echo \"Cleaning up workspace: {workspace}\"\n" +
        $"if [ ! -e \"{workspace}\" ]; then\n" +
        "  echo \"Workspace already clean\"\n" +
        "  exit 0\n" +
        "fi\n" +
        "for delay in 0 2 5; do\n" +
        "  sleep \"$delay\"\n" +
        $"  rm -rf \"{workspace}\"\n" +
        $"  if [ ! -e \"{workspace}\" ]; then\n" +
        "    echo \"Workspace cleaned successfully\"\n" +
        "    exit 0\n" +
        "  fi\n" +
        "  echo \"Workspace still present, retrying\"\n" +
        "done\n" +
        "echo \"Workspace cleanup failed after 3 attempts; left over:\" >&2\n" +
        $"ls -la \"{workspace}\" >&2\n" +
        "exit 1";

    internal static string BuildWindowsCleanupCommand(string workspace) =>
        $"Write-Output 'Cleaning up workspace: {workspace}'\n" +
        $"if (-not (Test-Path '{workspace}')) {{ Write-Output 'Workspace already clean'; exit 0 }}\n" +
        "foreach ($delay in 0, 2, 5) {\n" +
        "  Start-Sleep -Seconds $delay\n" +
        $"  Remove-Item -Recurse -Force '{workspace}' -ErrorAction SilentlyContinue\n" +
        $"  if (-not (Test-Path '{workspace}')) {{ Write-Output 'Workspace cleaned successfully'; exit 0 }}\n" +
        "  Write-Output 'Workspace still present, retrying'\n" +
        "}\n" +
        "Write-Output 'Workspace cleanup failed after 3 attempts.'\n" +
        "exit 1";

    /// <summary>
    /// F-032 / S-TECH-52: rewrites bash-style <c>&amp;&amp;</c> chains into PowerShell exit-code
    /// checks, skipping occurrences inside single- or double-quoted literals. The scanner mirrors
    /// PowerShell's own quoting rules so an escaped quote never desyncs the string state:
    /// <list type="bullet">
    /// <item>a backtick (<c>`</c>) escapes the next character outside single-quoted literals
    /// (including inside double-quoted strings), so <c>`"</c> is a literal quote, not a terminator;</item>
    /// <item>a doubled quote (<c>''</c> inside single, <c>""</c> inside double) is a literal quote, not
    /// a close-then-reopen.</item>
    /// </list>
    /// Without these rules a <c>&amp;&amp;</c> embedded in a string containing an escaped quote was
    /// rewritten as if it were a top-level operator, corrupting the command.
    /// </summary>
    internal static string RewritePwshAndChains(string command)
    {
        if (!command.Contains("&&", StringComparison.Ordinal)) return command;

        var sb = new StringBuilder(command.Length + 64);
        bool inSingle = false, inDouble = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (TryAppendBacktick(command, sb, ref i, c, inSingle)) continue;
            if (TryAppendSingleQuote(command, sb, ref i, c, ref inSingle, inDouble)) continue;
            if (TryAppendDoubleQuote(command, sb, ref i, c, inSingle, ref inDouble)) continue;
            if (TryAppendAndChain(command, sb, ref i, c, inSingle, inDouble)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool TryAppendBacktick(
        string command, StringBuilder output, ref int index, char current, bool inSingle)
    {
        if (current != '`' || inSingle || index + 1 >= command.Length) return false;
        output.Append(current).Append(command[index + 1]);
        index++;
        return true;
    }

    private static bool TryAppendSingleQuote(
        string command, StringBuilder output, ref int index, char current,
        ref bool inSingle, bool inDouble)
    {
        if (current != '\'' || inDouble) return false;
        if (inSingle && index + 1 < command.Length && command[index + 1] == '\'')
        {
            output.Append("''");
            index++;
            return true;
        }
        inSingle = !inSingle;
        output.Append(current);
        return true;
    }

    private static bool TryAppendDoubleQuote(
        string command, StringBuilder output, ref int index, char current,
        bool inSingle, ref bool inDouble)
    {
        if (current != '"' || inSingle) return false;
        if (inDouble && index + 1 < command.Length && command[index + 1] == '"')
        {
            output.Append("\"\"");
            index++;
            return true;
        }
        inDouble = !inDouble;
        output.Append(current);
        return true;
    }

    private static bool TryAppendAndChain(
        string command, StringBuilder output, ref int index, char current,
        bool inSingle, bool inDouble)
    {
        if (current != '&' || inSingle || inDouble
            || index + 1 >= command.Length || command[index + 1] != '&') return false;
        output.Append("; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE };");
        index++;
        return true;
    }

    // Phase 3: reject a working_directory that escapes the pipeline workspace (absolute path outside
    // it, or any parent-dir traversal). Keeps a step's filesystem footprint inside the sandbox root.
    internal static bool WorkingDirectoryEscapesWorkspace(string? workDir, string? workspace)
    {
        if (string.IsNullOrEmpty(workDir)) return false;
        var workSegments = PathSegments(workDir);
        if (workSegments.Contains("..", StringComparer.Ordinal)) return true;
        if (!IsAbsolutePath(workDir)) return false;
        if (string.IsNullOrEmpty(workspace)) return true; // absolute with no known workspace - unsafe

        var workspaceSegments = PathSegments(workspace);
        if (workspaceSegments.Contains("..", StringComparer.Ordinal)) return true;
        var comparison = UsesWindowsPathStyle(workDir, workspace)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedWorkDir = string.Join('/', workSegments.Where(s => s != "."));
        var normalizedWorkspace = string.Join('/', workspaceSegments.Where(s => s != "."));

        return !normalizedWorkDir.Equals(normalizedWorkspace, comparison)
            && !normalizedWorkDir.StartsWith(normalizedWorkspace + "/", comparison);
    }

    private static string[] PathSegments(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsAbsolutePath(string path) =>
        path.StartsWith('/') || path.StartsWith('\\')
        || path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';

    private static bool UsesWindowsPathStyle(string workDir, string workspace) =>
        workDir.Contains('\\', StringComparison.Ordinal)
        || workspace.Contains('\\', StringComparison.Ordinal)
        || workDir.Length >= 2 && workDir[1] == ':'
        || workspace.Length >= 2 && workspace[1] == ':';

    // P-07: Build every step inside its run workspace. An explicit working_directory may narrow
    // that location, while checkout only controls source preparation (handled by System:Prepare).
    internal static string BuildStepCommand(
        PipelineStepDefinition stepDef,
        Dictionary<string, string> stageVars,
        bool isWindows,
        IReadOnlySet<string>? secretKeys = null)
    {
        var vars = new Dictionary<string, string>(stageVars, StringComparer.OrdinalIgnoreCase);
        var refusal = BuildWorkingDirectoryRefusal(stepDef.WorkingDirectory, vars, isWindows);
        if (refusal is not null) return refusal;
        ReplaceSecretsWithPlaceholders(vars, secretKeys);

        var command = Substitute(stepDef.Shell, vars);
        if (isWindows)
            command = RewritePwshAndChains(command);

        var workDir = ResolveWorkingDirectory(stepDef.WorkingDirectory, vars);

        if (!string.IsNullOrEmpty(workDir))
        {
            // Every step enters its run-scoped workspace, using the repository checkout prepared by
            // System:Prepare. If that workspace disappears, recreate only the validated path and
            // warn so teardown can proceed while missing-source steps still fail honestly.
            command = isWindows
                ? $"if (-not [System.IO.Directory]::Exists('{workDir}')) {{ Write-Warning '{WorkspaceLostWarning}' }}\n"
                  + $"New-Item -ItemType Directory -Force -Path '{workDir}' | Out-Null\nSet-Location '{workDir}'\n{command}"
                : $"if [ ! -d \"{workDir}\" ]; then echo \"{WorkspaceLostWarning}\" >&2; fi\n"
                  + $"mkdir -p -- \"{workDir}\"\ncd \"{workDir}\"\n{command}";
        }

        return AddStrictShellMode(command, isWindows);
    }

    private static string? BuildWorkingDirectoryRefusal(
        string? configuredWorkDir,
        Dictionary<string, string> vars,
        bool isWindows)
    {
        if (string.IsNullOrEmpty(configuredWorkDir)) return null;
        var resolved = Substitute(configuredWorkDir, vars);
        if (!WorkingDirectoryEscapesWorkspace(resolved, vars.GetValueOrDefault("WORKSPACE"))) return null;
        return isWindows
            ? "$ErrorActionPreference='Stop'\nWrite-Error \"working_directory is outside the pipeline workspace - blocked.\"\nexit 1"
            : "echo \"working_directory is outside the pipeline workspace - blocked.\" >&2\nexit 1";
    }

    private static void ReplaceSecretsWithPlaceholders(
        IDictionary<string, string> vars,
        IReadOnlySet<string>? secretKeys)
    {
        if (secretKeys is null) return;
        foreach (var key in secretKeys)
        {
            if (vars.ContainsKey(key))
                vars[key] = PipelineSecretPlaceholder.Create(key);
        }
    }

    private static string? ResolveWorkingDirectory(
        string? configuredWorkDir,
        Dictionary<string, string> vars)
    {
        if (!string.IsNullOrEmpty(configuredWorkDir)) return Substitute(configuredWorkDir, vars);
        return vars.TryGetValue("WORKSPACE", out var workspace) && !string.IsNullOrWhiteSpace(workspace)
            ? workspace
            : null;
    }

    private static string AddStrictShellMode(string command, bool isWindows)
    {
        if (isWindows)
            return command.Contains("$ErrorActionPreference", StringComparison.Ordinal)
                ? command
                : $"$ErrorActionPreference = 'Stop'\n{command}";
        return command.StartsWith("set -e", StringComparison.Ordinal)
            ? command
            : $"set -e\n{command}";
    }
}
