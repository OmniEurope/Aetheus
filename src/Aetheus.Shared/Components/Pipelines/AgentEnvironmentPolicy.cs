// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The environment variable names an agent refuses to hand a step: each one changes how a shell, a loader or a
/// runtime starts (ENV is the startup file of <c>sh</c>, LD_PRELOAD injects a library...). The agent's command
/// validator enforces the list; the production setup reads it so it never names a library entry after one of them
/// (recette R-23: an <c>ENV</c> entry made every step of the deployment refused before it started).
/// </summary>
public static class AgentEnvironmentPolicy
{
    /// <summary>What a library entry is named after when the Compose variable it fills is a refused name.</summary>
    public const string ScopedPrefix = "DEPLOY_";

    public static readonly IReadOnlySet<string> DangerousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "GCONV_PATH", "BASH_ENV", "ENV",
        "PYTHONSTARTUP", "PERL5OPT", "RUBYOPT", "NODE_OPTIONS",
        "IFS", "SHELL", "HISTFILE",
        "PATH", "HOME", "USER", "LOGNAME", "CDPATH",
        "PROMPT_COMMAND", "SHELLOPTS", "BASHOPTS", "GLOBIGNORE",
        "DOTNET_STARTUP_HOOKS", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "CORECLR_ENABLE_PROFILING",
        "DOCKER_HOST", "DOCKER_CONTEXT", "GIT_CONFIG_GLOBAL",
        "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH",
        "PSModulePath", "PSModuleAutoloadingPreference", "COMSPEC", "PATHEXT", "PYTHONPATH"
    };

    /// <summary>The name a variable can travel under to a step: itself, or <see cref="ScopedPrefix"/> plus itself when
    /// the agent refuses it (ENV becomes DEPLOY_ENV).</summary>
    public static string StepSafeName(string name) =>
        DangerousNames.Contains(name) ? ScopedPrefix + name : name;
}
