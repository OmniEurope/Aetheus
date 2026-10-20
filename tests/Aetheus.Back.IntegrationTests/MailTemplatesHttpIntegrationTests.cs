// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recettes R1-74 and R1-75 over the real HTTP pipeline and PostgreSQL: an administrator edits, tests and
/// restores a template; a non-administrator is refused; a notification keeps its rich layout
/// in the recipient's language, read from the recipient's server-side preferences. Only the SMTP
/// sender is replaced, by one that keeps the mails it is handed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MailTemplatesHttpIntegrationTests(PostgresFixture fixture)
{
    private const string Root = "/api/mail-templates";

    [Fact]
    public async Task AnAdministrator_SavesTestsAndRestoresATemplate_TheTestInTheirOwnColours()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender();
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        var (preset, palette) = PaintedTheme();
        var admin = await SeedUserAsync(factory, admin: true, "\"en\"", Appearance(preset, palette));
        using var client = await IntegrationUsers.LoginClientAsync(factory, admin.Username);

        var put = await client.PutAsJsonAsync($"{Root}/NotificationChannelTest/fr",
            new SaveMailTemplateRequest { Subject = "Essai : {{ChannelName}}", Body = "<p>Canal <strong>{{ChannelName}}</strong></p>" }, ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await put.Content.ReadFromJsonAsync<MailTemplateDto>(IntegrationJsonOptions.Default, ct);
        Assert.True(saved!.IsCustomized);
        Assert.Equal(admin.Username, saved.UpdatedBy);

        var read = await client.GetFromJsonAsync<MailTemplateDto>($"{Root}/NotificationChannelTest/fr", IntegrationJsonOptions.Default, ct);
        Assert.Equal("Essai : {{ChannelName}}", read!.Subject);
        var list = await client.GetFromJsonAsync<List<MailTemplateSummaryDto>>(Root, IntegrationJsonOptions.Default, ct);
        Assert.Contains(list!, item => item is { Kind: MailTemplateKind.NotificationChannelTest, Language: "fr", IsCustomized: true });

        var test = await client.PostAsJsonAsync($"{Root}/NotificationChannelTest/fr/test",
            new SaveMailTemplateRequest { Subject = read.Subject, Body = read.Body }, ct);
        var result = await test.Content.ReadFromJsonAsync<MailTemplateTestResultDto>(IntegrationJsonOptions.Default, ct);
        Assert.Equal(NotificationTestStatus.Sent, result!.Status);
        Assert.Equal(admin.Email, result.To);
        var mail = Assert.Single(sender.Sent);
        Assert.Equal(admin.Email, mail.To);
        Assert.Equal("Essai : Équipe exploitation", mail.Subject);
        Assert.Contains("lang=\"fr\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(OmniMailTheme.From(preset.With(palette)).PageBackground, mail.HtmlBody, StringComparison.OrdinalIgnoreCase);

        var reset = await client.DeleteAsync($"{Root}/NotificationChannelTest/fr", ct);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var restored = await reset.Content.ReadFromJsonAsync<MailTemplateDto>(IntegrationJsonOptions.Default, ct);
        Assert.False(restored!.IsCustomized);
        Assert.Contains("{{ChannelName}}", restored.Subject, StringComparison.Ordinal);
        Assert.False((await client.GetFromJsonAsync<MailTemplateDto>($"{Root}/NotificationChannelTest/fr", IntegrationJsonOptions.Default, ct))!.IsCustomized);
    }

    [Fact]
    public async Task AVariableTheMailDoesNotOffer_IsRefused_AndNothingIsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var admin = await SeedUserAsync(factory, admin: true, null, null);
        using var client = await IntegrationUsers.LoginClientAsync(factory, admin.Username);

        var put = await client.PutAsJsonAsync($"{Root}/NotificationChannelTest/en",
            new SaveMailTemplateRequest { Subject = "{{ChannelName}}", Body = "<p>{{Headline}}</p>" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("{{Headline}}", await put.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        var read = await client.GetFromJsonAsync<MailTemplateDto>($"{Root}/NotificationChannelTest/en", IntegrationJsonOptions.Default, ct);
        Assert.False(read!.IsCustomized);
    }

    [Fact]
    public async Task ANonAdministrator_CanNeitherReadNorChangeTheTemplates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var user = await SeedUserAsync(factory, admin: false, null, null);
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Root, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Root}/NotificationChannelTest/en", ct)).StatusCode);
        var put = await client.PutAsJsonAsync($"{Root}/NotificationChannelTest/en",
            new SaveMailTemplateRequest { Subject = "x", Body = "<p>x</p>" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        var test = await client.PostAsJsonAsync($"{Root}/NotificationChannelTest/en/test",
            new SaveMailTemplateRequest { Subject = "x", Body = "<p>x</p>" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, test.StatusCode);
    }

    [Fact]
    public async Task ANotification_IsWrittenFromItsTemplate_InTheRecipientsLanguageAndColours_ReadFromPostgres()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender();
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        var (preset, palette) = PaintedTheme();
        var recipient = await SeedUserAsync(factory, admin: false, "\"fr-FR\"", Appearance(preset, palette));
        var notification = new NotificationEmail(recipient.Email.ToUpperInvariant(),
            "pipeline.failed in project Shop", "pipeline.failed",
            """{"PipelineRunId":12,"PipelineName":"deploy-shop","BuildNumber":3,"Status":"Failed"}""");

        // The default template first, then the one an administrator stored in PostgreSQL.
        await SendAsync(factory, notification, ct);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMailTemplateRepository>().UpsertAsync(MailTemplateKind.NotificationEvent, "fr",
                "Alerte {{Context}}", "<p>Bonjour.</p><p>{{Details}}</p><p>{{#button Link}}Ouvrir</p>", "admin", ct);
        }
        await SendAsync(factory, notification, ct);

        var mails = sender.Sent;
        Assert.Equal(2, mails.Count);
        var standard = mails[0];
        Assert.Equal("[Aetheus] Échec de l'exécution du pipeline : deploy-shop", standard.Subject);
        Assert.Contains("lang=\"fr\"", standard.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Numéro de build\t3", standard.TextBody, StringComparison.Ordinal);
        // Status banner and badge in the status colours, the mail itself in the recipient's colours.
        Assert.Contains("background-color: #fde8e6; color: #a3231a\"><span", standard.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("font-weight: 600; background-color: #fde8e6; color: #a3231a\">Failed</span>", standard.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Notification de la plateforme", standard.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(OmniMailTheme.From(preset.With(palette)).PageBackground, standard.HtmlBody, StringComparison.OrdinalIgnoreCase);
        var edited = mails[1];
        Assert.Equal("Alerte deploy-shop", edited.Subject);
        Assert.Contains("Bonjour.", edited.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Notification de la plateforme", edited.HtmlBody, StringComparison.Ordinal);
    }

    private static async Task SendAsync(WebApplicationFactory<Program> factory, NotificationEmail notification, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var emailer = scope.ServiceProvider.GetRequiredService<INotificationEmailer>();
        // The address as another system may spell it: the account is found whatever the case.
        var results = await emailer.SendAsync([notification], ct);
        Assert.Equal(EmailSendStatus.Sent, Assert.Single(results).Status);
    }

    private static (OmniThemePreset Preset, OmniThemePalette Palette) PaintedTheme()
    {
        var preset = OmniThemePresets.All[^1];
        var palette = OmniThemePalettes.All.First(item => OmniMailTheme.From(preset.With(item)).PageBackground != OmniMailTheme.Default.PageBackground);
        return (preset, palette);
    }

    private static string Appearance(OmniThemePreset preset, OmniThemePalette palette) =>
        $$"""{"theme":"light","preset":"{{preset.Name}}","palette":"{{palette.Name}}","font":"","density":"comfortable","textSize":"5","controlSize":"5","backdropMotion":"on"}""";

    private static WebApplicationFactory<Program> WithSender(AetheusWebApplicationFactory factory, IEmailSender sender) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(sender)));

    /// <summary>A user with an address and, when given, language and appearance preferences; an administrator when asked.</summary>
    private static async Task<(int Id, string Username, string Email)> SeedUserAsync(
        WebApplicationFactory<Program> factory, bool admin, string? languageJson, string? appearanceJson)
    {
        var ct = TestContext.Current.CancellationToken;
        var (id, username) = await IntegrationUsers.CreateAsync(factory.Services, admin ? "mail-admin" : "mail-user", []);
        var email = $"{username}@example.org";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(row => row.Id == id, ct);
        user.Email = email;
        if (admin)
        {
            var role = await db.Roles.FirstOrDefaultAsync(row => row.Name == "Admin", ct)
                ?? db.Roles.Add(new Role { Name = "Admin", Description = "Full access" }).Entity;
            await db.SaveChangesAsync(ct);
            db.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id });
        }
        if (languageJson is not null)
            db.UserPreferences.Add(new UserPreference { UserId = id, Key = "aetheus.language", Value = languageJson, ChangedAt = DateTime.UtcNow });
        if (appearanceJson is not null)
            db.UserPreferences.Add(new UserPreference { UserId = id, Key = "aetheus.appearance", Value = appearanceJson, ChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
        return (id, username, email);
    }

    /// <summary>Accepts every mail and keeps it, in place of the SMTP server.</summary>
    private sealed class CapturingSender : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> _sent = new();

        public IReadOnlyList<EmailMessage> Sent => [.. _sent];

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            _sent.Enqueue(message);
            return Task.FromResult(EmailSendResult.Sent("no-reply@example.org"));
        }

        public Task<IReadOnlyList<EmailSendResult>> SendBatchAsync(IReadOnlyList<EmailMessage> messages, CancellationToken ct = default)
        {
            foreach (var message in messages) _sent.Enqueue(message);
            return Task.FromResult<IReadOnlyList<EmailSendResult>>([.. messages.Select(_ => EmailSendResult.Sent("no-reply@example.org"))]);
        }
    }
}
