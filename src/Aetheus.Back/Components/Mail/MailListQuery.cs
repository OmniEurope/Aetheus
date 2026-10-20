// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

/// <summary> The column filters of a server's mail grids (domains, mailboxes, aliases). The keys
/// are the grids' column keys. </summary>
internal static class MailListQuery
{
    internal static readonly GridQueryMap<MailDomain> DomainColumns = new GridQueryMap<MailDomain>()
        .Text("name", d => d.Name)
        .Text("dkimSelector", d => d.DkimSelector);

    internal static readonly GridQueryMap<MailAccount> AccountColumns = new GridQueryMap<MailAccount>()
        .Text("email", a => a.Email)
        .Date("createdAt", a => a.CreatedAt);

    internal static readonly GridQueryMap<MailAlias> AliasColumns = new GridQueryMap<MailAlias>()
        .Text("sourceEmail", a => a.SourceEmail)
        .Text("destinationEmail", a => a.DestinationEmail);
}
