// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Recette R3-44: when a service refuses to start, the task says why without SSH. The agent appends the
/// unit's status (systemd prints its last journal lines, read as root through the <c>status</c> verb the
/// AETHEUS_SYSTEMCTL allow-list already grants) and, for the units Aetheus can check, the configuration
/// test of their root-owned helper (Apache configtest, the mail helper's <c>check</c>). Every command is a
/// fixed argv for <c>sudo -n</c>; a helper that is not installed on the host is skipped, never invented.
/// </summary>
internal static class ServiceStartDiagnostics
{
    internal const string ApacheConfigTestHelperPath = "/usr/local/lib/aetheus/aetheus-apache-configtest";

    /// <summary>Whether a failed <paramref name="kind"/> is worth explaining: a start or a restart.</summary>
    internal static bool Applies(OperationKind kind) => kind is OperationKind.ServiceStart or OperationKind.ServiceRestart;

    /// <summary>The <c>sudo</c> argv lists to run, in order, for <paramref name="unit"/> (a bare unit name).</summary>
    internal static IReadOnlyList<IReadOnlyList<string>> Commands(string unit, Func<string, bool> helperExists)
    {
        var commands = new List<IReadOnlyList<string>> { new[] { "-n", "/bin/systemctl", "status", unit } };
        var helper = unit switch
        {
            "apache2" => new[] { "-n", ApacheConfigTestHelperPath },
            "postfix" or "dovecot" => new[] { "-n", MailManageCommandBuilder.ManageHelperPath, "check" },
            _ => null
        };
        if (helper is not null && helperExists(helper[1])) commands.Add(helper);
        return commands;
    }
}
