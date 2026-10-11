// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary>
/// One e-mail to one recipient. <see cref="TextBody"/> is always sent; <see cref="HtmlBody"/>, when
/// given, goes beside it as the HTML alternative of a multipart/alternative message.
/// </summary>
public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);
