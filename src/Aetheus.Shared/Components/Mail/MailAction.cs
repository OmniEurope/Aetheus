// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Mail;

/// <summary>Mail service, queue and configuration actions.</summary>
public enum MailAction
{
    StartPostfix,
    StopPostfix,
    RestartPostfix,
    ReloadPostfix,
    StartDovecot,
    StopDovecot,
    RestartDovecot,
    ReloadDovecot,
    FlushQueue,
    ViewQueue,
    TestConfig,
    StartSpamFilter,
    StopSpamFilter,
    RestartSpamFilter,
    RestartOpenDkim,

    /// <summary>Shorter Postfix retry timings (mail-manage retry-tuning); values fixed in the helper.</summary>
    ApplyRetryTuning
}
