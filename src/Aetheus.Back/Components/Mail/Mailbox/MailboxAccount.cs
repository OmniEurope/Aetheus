// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>A mailbox of a server, and the host its IMAP and submission services answer on.</summary>
public sealed record MailboxAccount(int AccountId, int ServerId, string Email, string Host);
