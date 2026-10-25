// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// The wall clock the platform's mails state their times in. The daily recap's time zone setting is the
/// platform's (recette R-55 follow-up); the daily recap module registers the resolver, so the notifications
/// never reference that module (a delegate keeps the edge pointing downward).
/// </summary>
public delegate Task<TimeZoneInfo> MailTimeZoneResolver(CancellationToken ct);
