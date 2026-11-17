// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Mail;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Recette R-12: what the mail page calls a task it adopted on arrival, with the resources the page itself uses
/// when it launches the task (the backend names are the <see cref="ServerTaskNames"/> constants).</summary>
internal static class MailAdoptedTitles
{
    public static string? For(IStringLocalizer<AppStrings> l, string taskName) => ServerAdoptedTaskTitles.Resolve(taskName,
    [
        ServerAdoptedTaskTitles.KeyWith(l, ServerTaskNames.MailAddDomainPrefix, "AddDomain"),
        ServerAdoptedTaskTitles.Key(l, ServerTaskNames.MailRemoveDomainPrefix, "DeleteDomain"),
        ServerAdoptedTaskTitles.KeyWith(l, ServerTaskNames.MailAddAccountPrefix, "AddAccount"),
        ServerAdoptedTaskTitles.Key(l, ServerTaskNames.MailDeleteAccountPrefix, "DeleteAccount"),
        // "Mail - alias a@x → b@x" is called by its source, as the page does.
        ServerAdoptedTaskTitles.KeyWithHead(l, ServerTaskNames.MailAddAliasPrefix, "AddAlias", " → "),
        ServerAdoptedTaskTitles.Key(l, ServerTaskNames.MailRemoveAliasPrefix, "DeleteAlias"),
        // "Mail - rotate DKIM key for example.org (old → new)" is called by its domain.
        ServerAdoptedTaskTitles.KeyWithHead(l, ServerTaskNames.MailRotateDkimPrefix, "RotateDkimKey", " ("),
        ServerAdoptedTaskTitles.Key(l, ServerTaskNames.MailPrepareAdministratorAccess, "MailMasterAccessPrepare"),
        ServerAdoptedTaskTitles.Key(l, ServerTaskNames.MailSpamInstall, "MailSpamFilter"),
        ServerAdoptedTaskTitles.Key(l, ServerTaskNames.MailSpamThresholds, "MailSpamThresholds"),
        ServerAdoptedTaskTitles.Key(l, ServerTaskNames.MailLearnPrefix, "MailLearnTitle"),
        ServerAdoptedTaskTitles.Key(l, ServerTaskNames.MailActionTask(MailAction.StartSpamFilter), "MailSpamFilter"),
        ServerAdoptedTaskTitles.Key(l, ServerTaskNames.MailActionTask(MailAction.StopSpamFilter), "MailSpamFilter"),
        ServerAdoptedTaskTitles.Key(l, ServerTaskNames.MailActionTask(MailAction.RestartSpamFilter), "MailSpamFilter")
    ]);
}
