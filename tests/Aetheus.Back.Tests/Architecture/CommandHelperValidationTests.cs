// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Back.Components.Apache;
using Aetheus.Back.Components.Rkhunter;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Regression guard: every shell string produced by <c>*CommandHelper.Build*</c> methods that the
/// agent will route through <see cref="CommandValidator"/> must pass
/// <see cref="CommandValidator.IsAllowed"/> with the default allow-list. If a helper changes its
/// output shape (new binary, different quoting, extra flags), this test breaks BEFORE the command
/// silently fails in production.
///
/// Commands that are executed through elevated/sudo paths (mail setup, account management, DKIM,
/// portsentry setup) are validated separately as <b>expected rejections</b> - they contain shell
/// metacharacters (<c>$()</c>, <c>&gt;</c>, <c>;</c>) that the validator intentionally blocks.
/// Those commands reach the shell via the sudo-escalation pathway, not the standard executor.
/// If a refactor removes the metacharacters, the "rejected" test will break, signaling that the
/// command can (and should) be moved to the standard validator path.
/// </summary>
public class CommandHelperValidationTests
{
    private readonly CommandValidator _validator = new(Options.Create(new AetheusAgentOptions()));

    // Mail operations use typed commands through the root-owned helper; agent tests cover their
    // argument vectors.

    // Portsentry uses typed operations, covered by its executor and service tests rather than
    // shell-string validation.

    // ───────────────────────────────────────────────────────────────────
    // Rkhunter - getlogs is designed metacharacter-free so it PASSES the default allow-list (the
    // setup is the typed RkhunterSetup operation since R-119). Locks the helper<->allow-list
    // lockstep the helper comments claim, so a format drift breaks the build instead of the agent.
    // ───────────────────────────────────────────────────────────────────

    public static TheoryData<string, string> RkhunterCommands_Allowed => new()
    {
        { "GetLogs",         RkhunterCommandHelper.BuildGetLogsCommand(100) },
    };

    [Theory]
    [MemberData(nameof(RkhunterCommands_Allowed))]
    public void RkhunterCommand_IsAllowed(string label, string command)
    {
        Assert.True(_validator.IsAllowed(command),
            $"Rkhunter command '{label}' should pass validation but was rejected: {command}");
    }

    // ───────────────────────────────────────────────────────────────────
    // Apache - every Build* helper string. Service/getlogs carry shell
    // metacharacters (||, &&, 2>&1, 2>/dev/null) by design and reach the shell via the
    // elevated apache path, so the default validator must REJECT them (they must never
    // travel the standard unelevated executor). This closes the guard's Apache blind spot.
    // ───────────────────────────────────────────────────────────────────

    public static TheoryData<string, string> ApacheCommands_WithMetachars => new()
    {
        { "Start",          ApacheCommandHelper.BuildServiceCommand(ApacheAction.Start) },
        { "Stop",           ApacheCommandHelper.BuildServiceCommand(ApacheAction.Stop) },
        { "Restart",        ApacheCommandHelper.BuildServiceCommand(ApacheAction.Restart) },
        { "Reload",         ApacheCommandHelper.BuildServiceCommand(ApacheAction.Reload) },
        { "TestConfig",     ApacheCommandHelper.BuildServiceCommand(ApacheAction.TestConfig) },
        { "GetLogs_access", ApacheCommandHelper.BuildGetLogsCommand("access", 100) },
        { "GetLogs_error",  ApacheCommandHelper.BuildGetLogsCommand("error", 100) },
    };

    [Theory]
    [MemberData(nameof(ApacheCommands_WithMetachars))]
    public void ApacheCommand_WithMetachars_IsRejected(string label, string command)
    {
        Assert.False(_validator.IsAllowed(command),
            $"Apache command '{label}' contains shell metacharacters and must be rejected by the default validator: {command}");
    }
}
