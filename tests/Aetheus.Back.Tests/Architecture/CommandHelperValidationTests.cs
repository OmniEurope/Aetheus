// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Back.Components.Apache;
using Aetheus.Back.Components.Mail;
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

    // Mail no longer has any shell-string builders (PLAN-005): service control, queue, configuration
    // check, logs and spam-filter control dispatch typed OperationKinds through the root-owned
    // mail-manage helper. The argv coverage lives in MailManageCommandBuilderTests (agent).

    // Portsentry no longer has any shell-string builders: it dispatches exclusively via typed
    // OperationKind ops (setup / unblock / service / logs / status), covered by
    // PortsentryOperationExecutorTests / PortsentryServiceTests. Nothing to validate here anymore.

    // ───────────────────────────────────────────────────────────────────
    // Rkhunter - setup + getlogs are designed metacharacter-free so they PASS the default
    // allow-list (apt-get/rkhunter/sed/export/echo patterns + tail). Locks the helper<->allow-list
    // lockstep the helper comments claim, so a format drift breaks the build instead of the agent.
    // ───────────────────────────────────────────────────────────────────

    public static TheoryData<string, string> RkhunterCommands_Allowed => new()
    {
        { "Setup_noEmail",   RkhunterCommandHelper.BuildSetupCommand(null) },
        { "Setup_withEmail", RkhunterCommandHelper.BuildSetupCommand("admin@example.com") },
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
    // Apache - every Build* helper string. Service/site/module/getlogs all carry shell
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
        { "EnableSite",     ApacheCommandHelper.BuildEnableSiteCommand("mysite") },
        { "DisableSite",    ApacheCommandHelper.BuildDisableSiteCommand("mysite") },
        { "EnableModule",   ApacheCommandHelper.BuildEnableModuleCommand("proxy") },
        { "DisableModule",  ApacheCommandHelper.BuildDisableModuleCommand("proxy") },
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
