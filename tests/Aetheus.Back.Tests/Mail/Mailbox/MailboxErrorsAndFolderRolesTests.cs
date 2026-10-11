// SPDX-License-Identifier: EUPL-1.2
using System.Net.Sockets;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Exceptions;
using MailKit;
using MailKit.Security;

namespace Aetheus.Back.Tests;

/// <summary>Recette R1-17: MailKit failures become the API's own exceptions; folders get their role.</summary>
public sealed class MailboxErrorsAndFolderRolesTests
{
    [Fact]
    public void A_refused_login_is_a_conflict_with_the_server_reason()
    {
        var translated = MailboxErrors.Translate(new AuthenticationException("Authentication failed."), "mail.example.org", 993);

        var conflict = Assert.IsType<ConflictException>(translated);
        Assert.Contains("Authentication failed.", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreachable_or_tls_failures_are_bad_requests_and_missing_items_not_found()
    {
        Assert.IsType<BadRequestException>(MailboxErrors.Translate(new SocketException(10061), "h", 993));
        Assert.IsType<BadRequestException>(MailboxErrors.Translate(new SslHandshakeException("bad certificate"), "h", 993));
        Assert.IsType<NotFoundException>(MailboxErrors.Translate(new FolderNotFoundException("X"), "h", 993));
        Assert.IsType<NotFoundException>(MailboxErrors.Translate(new MessageNotFoundException("gone"), "h", 993));
    }

    [Fact]
    public void Cancellation_and_unknown_failures_are_left_unchanged()
    {
        Assert.Null(MailboxErrors.Translate(new OperationCanceledException(), "h", 993));
        Assert.Null(MailboxErrors.Translate(new InvalidOperationException(), "h", 993));
    }

    [Theory]
    [InlineData("INBOX", "INBOX", FolderAttributes.None, MailboxFolderRole.Inbox)]
    [InlineData("Envoyés", "Envoyés", FolderAttributes.Sent, MailboxFolderRole.Sent)]
    [InlineData("Trash", "Trash", FolderAttributes.None, MailboxFolderRole.Trash)]
    [InlineData("Spam", "Spam", FolderAttributes.None, MailboxFolderRole.Junk)]
    [InlineData("Projects.Sent", "Sent", FolderAttributes.None, MailboxFolderRole.Other)]
    [InlineData("Invoices", "Invoices", FolderAttributes.None, MailboxFolderRole.Other)]
    public void Folder_roles_come_from_special_use_then_top_level_names(
        string fullName, string name, FolderAttributes attributes, MailboxFolderRole expected)
    {
        Assert.Equal(expected, MailboxFolderRoles.Resolve(fullName, name, attributes));
    }

    [Fact]
    public void The_ports_are_those_of_the_installed_mail_stack()
    {
        Assert.Equal(993, MailboxPorts.Imaps);
        Assert.Equal(587, MailboxPorts.Submission);
    }
}
