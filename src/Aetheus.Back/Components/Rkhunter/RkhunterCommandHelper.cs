// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Rkhunter;

public static class RkhunterCommandHelper
{
    // The agent's CommandValidator runs every task command through an allow-list and rejects
    // ALL shell metacharacters EXCEPT `&&`, which it accepts only between otherwise-valid
    // segments. So multi-step commands here must be `&&`-chains of single, metacharacter-free,
    // allow-listed invocations - no `;`, `|`, `||`, `$()`, backticks, or redirections (`2>&1`,
    // `2>/dev/null`). The agent captures stdout and stderr separately, so redirections are
    // unnecessary anyway; each whitelisted segment is pinned in AetheusAgentOptions.
    public static string BuildActionCommand(RkhunterAction action) => action switch
    {
        RkhunterAction.UpdateDatabase => "rkhunter --update --nocolors",
        RkhunterAction.UpdateProperties => "rkhunter --propupd --nocolors",
        RkhunterAction.RunScan => "rkhunter --check --skip-keypress --nocolors",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    /// <summary>The warning address rule the agent and the root-owned helper apply too (R-119).</summary>
    public static bool IsValidEmail(string email) => RkhunterSetupEnv.IsValidMailOnWarning(email);

    // BuildSetupCommand was removed (recette R-119): its `apt-get install ... && rkhunter ... && sed -i` chain ran
    // without root and carried no operation kind. The setup dispatches typed OperationKind.RkhunterSetup.

    // A single metacharacter-free command. If the log is absent, `tail` exits non-zero and
    // writes its own "No such file" message to stderr (which the agent captures and surfaces)
    // - the old `2>/dev/null || echo …` fallback used metacharacters the validator rejects.
    public static string BuildGetLogsCommand(int lines) =>
        $"tail -n {lines} /var/log/rkhunter.log";
}
