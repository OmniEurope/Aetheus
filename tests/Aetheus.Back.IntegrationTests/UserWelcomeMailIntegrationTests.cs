// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-14 over real HTTP and PostgreSQL (owner decisions of 2026-10-28). An account is created without a
/// password: the welcome mail carries a single-use link, which sets the password once and is refused the second
/// time and after expiry; a mail that does not leave leaves the account created and says so. An administrator
/// sends a new link that voids the earlier one. "Forgotten password" answers the same for any address, mails a
/// one-hour link only to an active account, and is limited per address and per IP. Only the SMTP sender is
/// replaced. Bodies are raw JSON so the scenario reads the same against any version of the contract.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UserWelcomeMailIntegrationTests(PostgresFixture fixture)
{
    // What an older client would still send at creation: the server must not make it a way in.
    private const string AdminTypedPassword = "Typed-By-The-Admin-2026!";
    private const string ResetSubject = "Aetheus: choose a new password";
    private static readonly Regex Link = new(@"/login/set-password#token=([A-Za-z0-9_\-]+)");

    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    [Fact]
    public async Task ACreatedUser_CannotSignIn_UntilTheWelcomeLink_WhichSetsThePasswordOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var (email, created) = await CreateUserAsync(client, ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var welcome = (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, ct))!.WelcomeMail!;
        Assert.Equal(WelcomeMailStatus.Sent, welcome.Status);
        Assert.Equal(email, welcome.To);
        var mail = Assert.Single(sender.Sent);
        Assert.Equal(email, mail.To);
        Assert.DoesNotContain(AdminTypedPassword, mail.Subject + mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
        var token = Link.Match(mail.HtmlBody!).Groups[1].Value;
        Assert.NotEmpty(token);
        await using (var db = Db())
            Assert.False(await db.PasswordSetupTokens.AnyAsync(row => row.TokenHash == token, ct));

        // No password was set at creation: what the administrator typed is not a way in.
        IntegrationAuth.SetBearer(client, null);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = AdminTypedPassword }, ct)).StatusCode);

        var first = await client.PostAsJsonAsync("/api/auth/password-setup", new { token, newPassword = "Chosen-By-The-User-2026!" }, ct);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var again = await client.PostAsJsonAsync("/api/auth/password-setup", new { token, newPassword = "Another-Choice-2026!!" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(PasswordSetupErrorCodes.InvalidOrExpired,
            (await again.Content.ReadFromJsonAsync<ApiError>(IntegrationJsonOptions.Default, ct))!.Code);

        var signedIn = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Chosen-By-The-User-2026!" }, ct);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.False((await signedIn.Content.ReadFromJsonAsync<LoginResponse>(IntegrationJsonOptions.Default, ct))!.MustChangePassword);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Another-Choice-2026!!" }, ct)).StatusCode);
    }

    [Fact]
    public async Task AnExpiredWelcomeLink_IsRefused_AndTheAccountStaysClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var (email, created) = await CreateUserAsync(client, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var token = Link.Match(Assert.Single(sender.Sent).HtmlBody!).Groups[1].Value;

        await using (var db = Db())
        {
            var row = await db.PasswordSetupTokens.SingleAsync(t => t.User.Email == email, ct);
            row.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync(ct);
        }

        IntegrationAuth.SetBearer(client, null);
        var late = await client.PostAsJsonAsync("/api/auth/password-setup", new { token, newPassword = "Too-Late-Choice-2026!" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Too-Late-Choice-2026!" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = AdminTypedPassword }, ct)).StatusCode);
    }

    [Fact]
    public async Task WhenTheMailCannotLeave_TheUserIsStillCreated_AndTheAnswerSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Failed("554 relay refused"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var (email, created) = await CreateUserAsync(client, ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var welcome = (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, ct))!.WelcomeMail!;
        Assert.Equal(WelcomeMailStatus.Failed, welcome.Status);
        Assert.Equal("554 relay refused", welcome.Message);
        Assert.Single(sender.Sent);
        await using var db = Db();
        Assert.True(await db.Users.AnyAsync(u => u.Email == email, ct));
    }

    // --- Owner decision 2026-10-28 n°3: an administrator sends a new link ---

    [Fact]
    public async Task AnAdministratorsNewLink_VoidsTheWelcomeLink_IsRefusedToOthers_AndIsLimitedPerAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var (email, created) = await CreateUserAsync(client, ct);
        var userId = (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, ct))!.Id;
        var welcomeToken = Link.Match(Assert.Single(sender.Sent).HtmlBody!).Groups[1].Value;

        var sent = await client.PostAsync($"/api/auth/users/{userId}/password-link", null, ct);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var result = (await sent.Content.ReadFromJsonAsync<WelcomeMailResultDto>(IntegrationJsonOptions.Default, ct))!;
        Assert.Equal(WelcomeMailStatus.Sent, result.Status);
        Assert.Equal(email, result.To);
        Assert.Equal(2, sender.Sent.Count);
        var newToken = Link.Match(sender.Sent[1].HtmlBody!).Groups[1].Value;
        await using (var db = Db())
        {
            var row = await db.PasswordSetupTokens.AsNoTracking()
                .SingleAsync(t => t.UserId == userId && t.Purpose == PasswordSetupPurpose.AdminLink, ct);
            Assert.InRange(row.ExpiresAt - row.CreatedAt, TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(7));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "PasswordLinkSent" && a.EntityId == userId, ct));
        }

        // Someone who is not an administrator cannot send one.
        var reader = await IntegrationUsers.CreateAsync(factory.Services, "link-reader", []);
        using (var asReader = await IntegrationUsers.LoginClientAsync(factory, reader.Username))
            Assert.Equal(HttpStatusCode.Forbidden, (await asReader.PostAsync($"/api/auth/users/{userId}/password-link", null, ct)).StatusCode);

        // Within the hour, an account is sent at most five links by administrators; the sixth sends nothing.
        for (var i = 1; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/auth/users/{userId}/password-link", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync($"/api/auth/users/{userId}/password-link", null, ct)).StatusCode);
        Assert.Equal(6, sender.Sent.Count);

        // The welcome link and the earlier administrator links stopped working; only the newest one sets the password.
        IntegrationAuth.SetBearer(client, null);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/password-setup", new { token = welcomeToken, newPassword = "From-Old-Link-2026!" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/password-setup", new { token = newToken, newPassword = "From-Old-Link-2026!" }, ct)).StatusCode);
        var newest = Link.Match(sender.Sent[^1].HtmlBody!).Groups[1].Value;
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/password-setup", new { token = newest, newPassword = "From-New-Link-2026!" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "From-New-Link-2026!" }, ct)).StatusCode);
    }

    // --- Owner decision 2026-10-28 n°4: forgotten password ---

    [Fact]
    public async Task ForgottenPassword_AnswersTheSameForAnyAddress_AndMailsOnlyAnActiveAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var (active, _) = await CreateUserAsync(client, ct);
        var (inactive, _) = await CreateUserAsync(client, ct);
        await using (var db = Db())
        {
            var account = await db.Users.SingleAsync(u => u.Email == inactive, ct);
            account.IsActive = false;
            await db.SaveChangesAsync(ct);
        }
        IntegrationAuth.SetBearer(client, null);
        var noAccountBefore = await CountAuditAsync("PasswordResetRequested.NoAccount", ct);

        var answers = new List<(HttpStatusCode Status, string Body)>();
        foreach (var address in new[] { $"nobody-{Guid.NewGuid():N}@example.org", inactive, active.ToUpperInvariant() })
        {
            var response = await client.PostAsJsonAsync("/api/auth/password-reset", new { email = address }, ct);
            answers.Add((response.StatusCode, await response.Content.ReadAsStringAsync(ct)));
        }

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.Accepted, answer.Status));
        Assert.Single(answers.Select(answer => answer.Body).Distinct());
        await WaitUntilAsync(async () => sender.Sent.Any(m => m.Subject == ResetSubject)
            && await CountAuditAsync("PasswordResetRequested.NoAccount", ct) >= noAccountBefore + 2, ct);
        var resetMail = Assert.Single(sender.Sent, m => m.Subject == ResetSubject);
        Assert.Equal(active, resetMail.To);
        await using (var db = Db())
            Assert.False(await db.AuditLogs.AnyAsync(a => a.Action.StartsWith("PasswordResetRequested") && a.Details!.Contains("@"), ct));
    }

    [Fact]
    public async Task AResetLink_LastsOneHour_AndSetsThePasswordOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var (email, _) = await CreateUserAsync(client, ct);
        IntegrationAuth.SetBearer(client, null);

        var token = await RequestResetTokenAsync(client, sender, email, 1, ct);
        await using (var db = Db())
        {
            var row = await db.PasswordSetupTokens.AsNoTracking()
                .SingleAsync(t => t.User.Email == email && t.Purpose == PasswordSetupPurpose.Reset, ct);
            Assert.InRange(row.ExpiresAt - row.CreatedAt, TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        }
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/password-setup", new { token, newPassword = "Reset-Choice-2026!!" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/password-setup", new { token, newPassword = "Reset-Again-2026!!!" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Reset-Choice-2026!!" }, ct)).StatusCode);

        // A second link, left past its hour, is refused and changes nothing.
        var late = await RequestResetTokenAsync(client, sender, email, 2, ct);
        await using (var db = Db())
        {
            var row = await db.PasswordSetupTokens
                .SingleAsync(t => t.User.Email == email && t.Purpose == PasswordSetupPurpose.Reset && t.UsedAt == null, ct);
            row.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync(ct);
        }
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/password-setup", new { token = late, newPassword = "Too-Late-Reset-2026!" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Reset-Choice-2026!!" }, ct)).StatusCode);
    }

    [Fact]
    public async Task TheFourthResetWithinTheHour_IsAnsweredTheSame_ButSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        var (email, created) = await CreateUserAsync(client, ct);
        var userId = (await created.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, ct))!.Id;
        IntegrationAuth.SetBearer(client, null);

        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/password-reset", new { email }, ct)).StatusCode);

        await using var db = Db();
        await WaitUntilAsync(() => db.AuditLogs.AnyAsync(a => a.Action == "PasswordResetRequested.RateLimited" && a.EntityId == userId, ct), ct);
        Assert.Equal(3, sender.Sent.Count(m => m.Subject == ResetSubject));
    }

    [Fact]
    public async Task ForgottenPassword_IsLimitedPerAddressOfTheCaller_TheSixthRequestOfTheMinuteIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:Disabled", "false"));
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Accepted,
                (await client.PostAsJsonAsync("/api/auth/password-reset", new { email = $"probe-{i}-{Guid.NewGuid():N}@example.org" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync("/api/auth/password-reset", new { email = $"probe-6-{Guid.NewGuid():N}@example.org" }, ct)).StatusCode);
    }

    private async Task<int> CountAuditAsync(string action, CancellationToken ct)
    {
        await using var db = Db();
        return await db.AuditLogs.CountAsync(a => a.Action == action, ct);
    }

    private static async Task<string> RequestResetTokenAsync(HttpClient client, CapturingSender sender, string email, int expected, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/password-reset", new { email }, ct)).StatusCode);
        await WaitUntilAsync(() => Task.FromResult(sender.Sent.Count(m => m.Subject == ResetSubject) >= expected), ct);
        return Link.Match(sender.Sent.Where(m => m.Subject == ResetSubject).ElementAt(expected - 1).HtmlBody!).Groups[1].Value;
    }

    // The forgotten-password work runs on the background queue, after the answer: wait for its trace.
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The queued forgotten-password work left no trace within 15 seconds.");
            await Task.Delay(100, ct);
        }
    }

    private static async Task<(string Email, HttpResponseMessage Created)> CreateUserAsync(HttpClient client, CancellationToken ct)
    {
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, ct));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"welcome-{suffix}@example.org";
        var created = await client.PostAsJsonAsync("/api/users", new
        {
            username = $"welcome-{suffix}",
            password = AdminTypedPassword,
            email,
            organizationRoles = await IntegrationUsers.InDefaultOrganizationAsync(client, "Reader")
        }, ct);
        return (email, created);
    }

    private static WebApplicationFactory<Program> WithSender(AetheusWebApplicationFactory factory, IEmailSender sender) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(sender)));

    /// <summary>Keeps every mail it is handed and answers the given result, in place of the SMTP server.</summary>
    private sealed class CapturingSender(EmailSendResult answer) : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> _sent = new();

        public IReadOnlyList<EmailMessage> Sent => [.. _sent];

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            _sent.Enqueue(message);
            return Task.FromResult(answer);
        }

        public Task<IReadOnlyList<EmailSendResult>> SendBatchAsync(IReadOnlyList<EmailMessage> messages, CancellationToken ct = default)
        {
            foreach (var message in messages) _sent.Enqueue(message);
            return Task.FromResult<IReadOnlyList<EmailSendResult>>([.. messages.Select(_ => answer)]);
        }
    }
}
