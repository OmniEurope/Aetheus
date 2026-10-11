// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.MailboxUi;

/// <summary>Recette R1-17: unlock, compose and new-folder dialogs, the reading pane and the client settings.</summary>
public sealed class MailboxDialogsTests : BunitContext
{
    private const string Base = "api/servers/7/mail/accounts/3/mailbox";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public MailboxDialogsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    [Fact]
    public async Task Unlock_sends_the_password_and_closes_with_the_access_once_accepted()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Base + "/unlock", new MailboxAccessDto { AccountId = 3, IsUnlocked = true });
        var cut = Render<MailboxUnlockDialog>(parameters => parameters.Add(dialog => dialog.ServerId, 7).Add(dialog => dialog.AccountId, 3));
        BunitTestHelper.Interactive(this);

        cut.Find("#mailbox-unlock-password").Input("typed-secret");
        await cut.Find("form").SubmitAsync();

        Assert.Contains("typed-secret", _handler.LastRequestBody, StringComparison.Ordinal);
        Assert.True(Assert.IsType<MailboxAccessDto>(_dialog.LastCloseResult).IsUnlocked);
    }

    [Fact]
    public async Task A_refused_password_is_said_in_the_field_and_the_dialog_stays_open()
    {
        _handler.SetResponse(HttpMethod.Post, Base + "/unlock", HttpStatusCode.Conflict);
        var cut = Render<MailboxUnlockDialog>(parameters => parameters.Add(dialog => dialog.ServerId, 7).Add(dialog => dialog.AccountId, 3));
        BunitTestHelper.Interactive(this);
        cut.Find("#mailbox-unlock-password").Input("wrong");

        await cut.Find("form").SubmitAsync();

        Assert.Equal(0, _dialog.CloseCount);
        Assert.Contains("MailboxUnlockRefused", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void No_field_of_the_unlock_dialog_takes_the_focus()
    {
        var cut = Render<MailboxUnlockDialog>(parameters => parameters.Add(dialog => dialog.ServerId, 7).Add(dialog => dialog.AccountId, 3));

        Assert.Empty(cut.FindAll("[autofocus]"));
    }

    private IRenderedComponent<MailboxComposeDialog> RenderCompose(MailboxComposeMode mode, MailboxDraftDto draft) =>
        Render<MailboxComposeDialog>(parameters => parameters
            .Add(dialog => dialog.ServerId, 7).Add(dialog => dialog.AccountId, 3).Add(dialog => dialog.From, "me@example.org")
            .Add(dialog => dialog.Mode, mode).Add(dialog => dialog.Draft, draft)
            .Add(dialog => dialog.SourceFolder, "INBOX").Add(dialog => dialog.SourceUid, 5u));

    [Fact]
    public void The_compose_dialog_starts_from_the_draft()
    {
        var cut = RenderCompose(MailboxComposeMode.Forward, new MailboxDraftDto
        {
            Subject = "Fwd: Planning",
            Body = "quoted",
            ForwardedAttachments = [new MailboxAttachmentDto { FileName = "report.pdf" }]
        });

        Assert.Equal("Fwd: Planning", cut.Find("#mailbox-compose-subject").GetAttribute("value"));
        Assert.Equal("me@example.org", cut.Find("#mailbox-compose-from").GetAttribute("value"));
        Assert.Single(cut.FindAll(".mailbox-forwarded"));
        Assert.Empty(cut.FindAll("[autofocus]"));
    }

    [Fact]
    public async Task Without_any_recipient_nothing_is_sent()
    {
        var cut = RenderCompose(MailboxComposeMode.New, new MailboxDraftDto());
        BunitTestHelper.Interactive(this);

        await cut.Find("form").SubmitAsync();

        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("/send", StringComparison.Ordinal));
        Assert.Contains("MailboxRecipientRequired", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_is_sent_with_its_source_and_closes_with_the_result()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Base + "/send", new MailboxSendResultDto { SavedToSent = true });
        var cut = RenderCompose(MailboxComposeMode.Reply, new MailboxDraftDto { To = "alice@example.com", Subject = "Re: Planning", Body = "> hi" });

        await cut.InvokeAsync(() => cut.Instance.SendAsync());

        var sent = JsonSerializer.Deserialize<MailboxSendRequest>(_handler.LastRequestBody!, JsonOptions.Web)!;
        Assert.Equal((MailboxComposeMode.Reply, "alice@example.com", "INBOX", 5u), (sent.Mode, sent.To, sent.SourceFolder, sent.SourceUid));
        Assert.True(Assert.IsType<MailboxSendResultDto>(_dialog.LastCloseResult).SavedToSent);
    }

    [Fact]
    public async Task A_refused_send_keeps_the_dialog_open()
    {
        _handler.SetResponse(HttpMethod.Post, Base + "/send", HttpStatusCode.BadRequest);
        var cut = RenderCompose(MailboxComposeMode.New, new MailboxDraftDto { To = "a@example.com" });

        await cut.InvokeAsync(() => cut.Instance.SendAsync());

        Assert.Equal(0, _dialog.CloseCount);
    }

    private static void Attach(IRenderedComponent<MailboxComposeDialog> cut, params InputFileContent[] files) =>
        cut.FindComponent<InputFile>().UploadFiles(files);

    [Fact]
    public async Task The_attached_files_are_sent_with_their_names_and_bytes()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Base + "/send", new MailboxSendResultDto { SavedToSent = true });
        var cut = RenderCompose(MailboxComposeMode.New, new MailboxDraftDto { To = "a@example.com" });

        Attach(cut,
            InputFileContent.CreateFromBinary([1, 2, 3], "report.pdf", contentType: "application/pdf"),
            InputFileContent.CreateFromText("hello", "notes.txt"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".omni-upload__file").Count));
        await cut.InvokeAsync(() => cut.Instance.SendAsync());

        var sent = JsonSerializer.Deserialize<MailboxSendRequest>(_handler.LastRequestBody!, JsonOptions.Web)!;
        Assert.Equal(["report.pdf", "notes.txt"], sent.Attachments.Select(attachment => attachment.FileName).ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, sent.Attachments[0].Content);
        Assert.Equal("hello"u8.ToArray(), sent.Attachments[1].Content);
    }

    [Fact]
    public async Task A_file_removed_before_sending_is_not_sent()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Base + "/send", new MailboxSendResultDto { SavedToSent = true });
        var cut = RenderCompose(MailboxComposeMode.Reply, new MailboxDraftDto { To = "a@example.com" });
        Attach(cut,
            InputFileContent.CreateFromText("first", "first.txt"),
            InputFileContent.CreateFromText("second", "second.txt"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".omni-upload__file").Count));

        cut.FindAll("button.omni-upload__remove")[0].Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".omni-upload__file")));
        await cut.InvokeAsync(() => cut.Instance.SendAsync());

        var sent = JsonSerializer.Deserialize<MailboxSendRequest>(_handler.LastRequestBody!, JsonOptions.Web)!;
        var only = Assert.Single(sent.Attachments);
        Assert.Equal(("second.txt", "second"), (only.FileName, System.Text.Encoding.UTF8.GetString(only.Content)));
    }

    [Fact]
    public void Files_over_the_total_limit_are_refused_and_nothing_is_attached()
    {
        var cut = RenderCompose(MailboxComposeMode.New, new MailboxDraftDto { To = "a@example.com" });
        var eightMegabytes = new byte[8 * 1024 * 1024];

        Attach(cut,
            InputFileContent.CreateFromBinary(eightMegabytes, "a.bin"),
            InputFileContent.CreateFromBinary(eightMegabytes, "b.bin"),
            InputFileContent.CreateFromBinary(eightMegabytes, "c.bin"));

        cut.WaitForAssertion(() => Assert.Contains("MailboxAttachmentsTooLarge", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".omni-upload__file"));
        Assert.Empty(cut.Instance.Attachments);
    }

    [Fact]
    public void The_upload_field_is_bounded_by_the_shared_limits()
    {
        var cut = RenderCompose(MailboxComposeMode.New, new MailboxDraftDto());

        var upload = cut.FindComponent<OmniUpload>().Instance;
        Assert.Equal((true, MailboxLimits.MaxAttachmentCount, (long)MailboxLimits.MaxAttachmentBytes),
            (upload.Multiple, upload.MaximumFiles, upload.MaximumFileSize));
        Assert.Contains("MailboxAttachmentsHint", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_folder_dialog_closes_with_the_trimmed_name()
    {
        var cut = Render<MailboxFolderDialog>();

        cut.Find("#mailbox-folder-name").Input("  Invoices ");
        await cut.InvokeAsync(() => cut.Instance.Submit());

        Assert.Equal("Invoices", _dialog.LastCloseResult);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a.b")]
    [InlineData("")]
    public void The_folder_name_rule_refuses_hierarchy_characters(string name)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var model = new MailboxFolderModel { Name = name };
        var valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            model, new System.ComponentModel.DataAnnotations.ValidationContext(model), results, validateAllProperties: true);

        Assert.False(valid);
    }

    [Fact]
    public void The_reader_shows_text_attachments_and_disables_changes_for_a_reader_without_write_right()
    {
        var downloaded = new List<MailboxAttachmentDto>();
        var cut = Render<MailboxReader>(parameters => parameters
            .Add(reader => reader.Message, new MailboxMessageDto
            {
                Subject = "Plain",
                Text = "line one\nline two",
                From = "a@example.com",
                Attachments = [new MailboxAttachmentDto { Index = 0, FileName = "a.txt" }]
            })
            .Add(reader => reader.CanWrite, false)
            .Add(reader => reader.OnDownload, attachment => downloaded.Add(attachment)));

        Assert.Equal("line one\nline two", cut.Find("pre.mailbox-text").TextContent);
        Assert.Empty(cut.FindAll(".mailbox-html"));
        Assert.Empty(cut.FindAll(".mailbox-images-blocked"));
        Assert.All(cut.FindAll(".mailbox-reader-actions button"), button => Assert.True(button.HasAttribute("disabled")));
        cut.FindAll(".mailbox-attachments button").Single().Click();
        Assert.Equal("a.txt", Assert.Single(downloaded).FileName);
    }

    [Fact]
    public void The_client_settings_name_the_host_ports_and_full_address()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers/7/mail/client-settings",
            new MailClientSettingsDto { Host = "mail.example.org", ImapPort = 993, SubmissionPort = 587 });

        var cut = Render<MailClientSettings>(parameters => parameters.Add(settings => settings.ServerId, 7).Add(settings => settings.Email, "me@example.org"));

        cut.WaitForAssertion(() => Assert.Contains("mail.example.org", cut.Markup, StringComparison.Ordinal));
        var text = cut.Find(".mail-client-settings").TextContent;
        Assert.Contains("993", text, StringComparison.Ordinal);
        Assert.Contains("587", text, StringComparison.Ordinal);
        Assert.Contains("me@example.org", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_settings_say_when_they_could_not_be_read()
    {
        _handler.SetResponse(HttpMethod.Get, "api/servers/7/mail/client-settings", HttpStatusCode.Forbidden);

        var cut = Render<MailClientSettings>(parameters => parameters.Add(settings => settings.ServerId, 7));

        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Markup, StringComparison.Ordinal));
    }
}
