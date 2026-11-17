// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary>
/// Audit decision 2026-10-12 (option a), through the real HTTP pipeline: the reading frame loads the sanitized
/// message document by its single-use ticket, under the document's own policy instead of the API's, and
/// nothing else opens it (no ticket, a used, expired or forged one, a locked mailbox, a JWT alone).
/// </summary>
public sealed class MailboxDocumentEndpointIntegrationTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private const string Raw =
        "<p style=\"color: red\">Bonjour</p><script>alert(1)</script><style>p { color: blue }</style>" +
        "<img src=\"https://tracker.example/pixel.png\" onerror=\"alert(2)\">";

    private static readonly MailboxCredentialKey s_reader = new("Bearer:42", 7, 3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string IssueFor(IServiceProvider services, MailboxCredentialKey key)
    {
        services.GetRequiredService<IMailboxCredentialStore>().Store(key, "mailbox-pass");
        var html = MailboxHtmlSanitizer.Sanitize(Raw, new Dictionary<string, string>()).Html;
        return services.GetRequiredService<IMailboxDocumentTickets>().Issue(key, MailboxDocumentPolicy.Wrap(html));
    }

    [Fact]
    public async Task The_ticket_holder_gets_the_sanitized_document_under_its_own_policy()
    {
        var ticket = IssueFor(factory.Services, s_reader);
        using var response = await factory.CreateClient().GetAsync($"/api/mail/documents/{ticket}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(factory.Services.GetRequiredService<MailboxDocumentPolicy>().ContentSecurityPolicy, csp);
        Assert.StartsWith(
            "default-src 'none'; style-src-attr 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors ",
            csp, StringComparison.Ordinal);
        Assert.Contains("https://localhost:5401", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("*", csp, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("X-Frame-Options"));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.True(response.Headers.CacheControl?.NoStore);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.StartsWith("<!DOCTYPE html>", body, StringComparison.Ordinal);
        Assert.Contains("<p style=\"color:", body, StringComparison.Ordinal);
        Assert.Contains("Bonjour", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.example", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_used_ticket_is_refused()
    {
        var ticket = IssueFor(factory.Services, s_reader with { AccountId = 4 });
        var client = factory.CreateClient();
        using (var first = await client.GetAsync($"/api/mail/documents/{ticket}", Ct))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.GetAsync($"/api/mail/documents/{ticket}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.DoesNotContain("Bonjour", await second.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forged_ticket_is_refused_even_with_a_valid_jwt()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CustomWebApplicationFactory.GenerateTestToken());

        using var response = await client.GetAsync("/api/mail/documents/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task No_ticket_opens_nothing()
    {
        using var response = await factory.CreateClient().GetAsync("/api/mail/documents/", Ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_ticket_is_refused_once_its_reader_no_longer_holds_the_mailbox_password()
    {
        var key = s_reader with { AccountId = 5 };
        var ticket = IssueFor(factory.Services, key);
        // A lock, the password's expiry or the end of the reader's session all forget the held password.
        factory.Services.GetRequiredService<IMailboxCredentialStore>().Forget(key);

        using var response = await factory.CreateClient().GetAsync($"/api/mail/documents/{ticket}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_ticket_is_refused()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IMailboxDocumentTickets>(new MailboxDocumentTickets(time))));
        var ticket = IssueFor(host.Services, s_reader with { AccountId = 6 });

        time.Advance(MailboxDocumentTickets.Lifetime);
        using var response = await host.CreateClient().GetAsync($"/api/mail/documents/{ticket}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_without_the_mailbox_read_right_gets_no_message_and_so_no_ticket()
    {
        const int serverId = 7101;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Servers.AnyAsync(server => server.Id == serverId, Ct))
            {
                db.Servers.Add(new Server
                {
                    Id = serverId,
                    Name = "mail-document-server",
                    Type = ServerType.Docker,
                    Status = ServerStatus.Online,
                    IpAddress = "10.0.0.71",
                    AgentVersion = "1.0.0",
                    OsDescription = "Linux",
                    LastHeartbeat = DateTime.UtcNow
                });
                await db.SaveChangesAsync(Ct);
            }
        }
        var url = $"/api/servers/{serverId}/mail/accounts/3/mailbox/messages/5?folder=INBOX";

        using var anonymous = await factory.CreateClient().GetAsync(url, Ct);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokenFactory.UserToken("outsider"));
        using var outsider = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
        Assert.DoesNotContain("api/mail/documents/", await outsider.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_other_routes_keep_the_api_policy()
    {
        using var response = await factory.CreateClient().GetAsync("/health/live", Ct);

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.StartsWith("default-src 'self'; ", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }
}
