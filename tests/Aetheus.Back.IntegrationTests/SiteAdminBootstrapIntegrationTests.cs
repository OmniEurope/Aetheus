// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Aetheus.Back.Data;
using Aetheus.Back.Services.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Owner decisions of 2026-10-28, over real HTTP and PostgreSQL: the address of <c>Auth:AdminEmail</c> becomes
/// site Admin at its first sign-in (or first password setup) only while no administrator exists and the bootstrap
/// is not locked; the lock is a database row, so changing the configured address afterwards makes nobody Admin.
/// Whoever newly becomes site Admin, by the bootstrap or through the user APIs, is told by mail, and a mail that
/// does not leave never undoes the grant. Only the SMTP sender is replaced.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteAdminBootstrapIntegrationTests(PostgresFixture fixture)
{
    private const string MarkerKey = "Auth:SiteAdminBootstrap";
    private const string GrantedSubject = "Aetheus: you are now a site administrator";
    private static readonly Regex Link = new(@"/login/set-password#token=([A-Za-z0-9_\-]+)");

    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    [Fact]
    public async Task TheSeed_MakesTheConfiguredAddressAdmin_AndLocksTheBootstrap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        await using var db = Db();
        Assert.True(await IsAdminAsync(db, IntegrationAuth.AdminEmail, ct));
        Assert.True(await db.AppSettings.AnyAsync(s => s.Key == MarkerKey, ct));
    }

    [Fact]
    public async Task WithNoAdministratorAndNoLock_TheConfiguredAddress_BecomesAdminAtItsFirstSignIn_AndIsMailedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        await ForgetEveryAdministratorAndTheLockAsync(ct);

        var token = await IntegrationAuth.LoginAsAdminAsync(client, ct);

        IntegrationAuth.SetBearer(client, token);
        var me = await client.GetFromJsonAsync<UserDto>("/api/users/me", IntegrationJsonOptions.Default, ct);
        Assert.Contains("Admin", me!.Roles);
        await using (var db = Db())
        {
            Assert.True(await IsAdminAsync(db, IntegrationAuth.AdminEmail, ct));
            Assert.True(await db.AppSettings.AnyAsync(s => s.Key == MarkerKey, ct));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "SiteAdminGranted" && a.EntityId == me.Id, ct));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "SiteAdminMailSent" && a.EntityId == me.Id, ct));
        }
        var mail = Assert.Single(sender.Sent, m => m.Subject == GrantedSubject);
        Assert.Equal(IntegrationAuth.AdminEmail, mail.To);

        // The next sign-in changes nothing and sends nothing: the bootstrap is done.
        IntegrationAuth.SetBearer(client, null);
        await IntegrationAuth.LoginAsAdminAsync(client, ct);
        Assert.Single(sender.Sent, m => m.Subject == GrantedSubject);
    }

    [Fact]
    public async Task WithNoAdministratorAndNoLock_TheFirstPasswordSetupOfTheConfiguredAddress_MakesItAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/api/auth/password-reset", new { email = IntegrationAuth.AdminEmail }, ct)).StatusCode);
        await WaitUntilAsync(() => sender.Sent.Any(m => Link.IsMatch(m.HtmlBody ?? string.Empty)), ct);
        var token = Link.Match(sender.Sent.First(m => Link.IsMatch(m.HtmlBody ?? string.Empty)).HtmlBody!).Groups[1].Value;
        await ForgetEveryAdministratorAndTheLockAsync(ct);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/password-setup", new { token, newPassword = "Owner-Chosen-Pwd-2026!" }, ct)).StatusCode);

        await using var db = Db();
        Assert.True(await IsAdminAsync(db, IntegrationAuth.AdminEmail, ct));
        Assert.True(await db.AppSettings.AnyAsync(s => s.Key == MarkerKey, ct));
        Assert.Equal(IntegrationAuth.AdminEmail, Assert.Single(sender.Sent, m => m.Subject == GrantedSubject).To);
    }

    [Fact]
    public async Task OnceLocked_ChangingTheConfiguredAddress_NeverMakesAnotherAccountAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        (int Id, string Username) other;
        await using (var seeded = new AetheusWebApplicationFactory(fixture.ConnectionString))
        {
            using var seededClient = seeded.CreateClient();
            other = await IntegrationUsers.CreateAsync(seeded.Services, "bootstrap-other", []);
        }
        var otherEmail = $"{other.Username}@example.com";

        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var restarted = new AetheusWebApplicationFactory(fixture.ConnectionString, resetSchema: false);
        await using var factory = WithSender(restarted, sender, otherEmail);
        using var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginByEmailAsync(client, otherEmail, IntegrationUsers.Password, ct));

        var me = await client.GetFromJsonAsync<UserDto>("/api/users/me", IntegrationJsonOptions.Default, ct);
        Assert.DoesNotContain("Admin", me!.Roles);
        Assert.DoesNotContain(sender.Sent, m => m.Subject == GrantedSubject);
        await using var db = Db();
        Assert.False(await IsAdminAsync(db, otherEmail, ct));
        Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == "SiteAdminGranted" && a.EntityId == other.Id, ct));
    }

    /// <summary>Production: administrators exist from before the bootstrap was written. The first start locks it
    /// without granting anything, so the configured address does not become Admin at its sign-in.</summary>
    [Fact]
    public async Task WhenAnAdministratorAlreadyExists_StartupLocksTheBootstrap_WithoutGrantingAnything()
    {
        var ct = TestContext.Current.CancellationToken;
        (int Id, string Username) other;
        await using (var seeded = new AetheusWebApplicationFactory(fixture.ConnectionString))
        {
            using var seededClient = seeded.CreateClient();
            other = await IntegrationUsers.CreateAsync(seeded.Services, "bootstrap-prod", []);
        }
        await using (var db = Db())
            await db.AppSettings.Where(s => s.Key == MarkerKey).ExecuteDeleteAsync(ct);
        var otherEmail = $"{other.Username}@example.com";

        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var restarted = new AetheusWebApplicationFactory(fixture.ConnectionString, resetSchema: false);
        await using var factory = WithSender(restarted, sender, otherEmail);
        using var client = factory.CreateClient();
        await using (var db = Db())
            Assert.True(await db.AppSettings.AnyAsync(s => s.Key == MarkerKey, ct));

        await IntegrationAuth.LoginByEmailAsync(client, otherEmail, IntegrationUsers.Password, ct);

        await using (var db = Db())
        {
            Assert.False(await IsAdminAsync(db, otherEmail, ct));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "SiteAdminBootstrapLocked", ct));
        }
        Assert.DoesNotContain(sender.Sent, m => m.Subject == GrantedSubject);
    }

    [Fact]
    public async Task AnAccountNewlyMadeSiteAdmin_IsMailed_OnEveryPath_ButNotWhenTheRoleIsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Sent("no-reply@example.org"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, ct));
        var reader = await IntegrationUsers.InDefaultOrganizationAsync(client, "Reader");

        // Created as Admin.
        var created = await CreateAsync(client, "grant-create", ["Admin"], [], ct);
        Assert.Equal(created.Email, Assert.Single(sender.Sent, m => m.Subject == GrantedSubject).To);

        // Saved again with the same roles: no new mail.
        Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(client, created, ["Admin"], ct)).StatusCode);
        Assert.Single(sender.Sent, m => m.Subject == GrantedSubject);

        // Made Admin from the edit form.
        var edited = await CreateAsync(client, "grant-edit", [], reader, ct);
        Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(client, edited, ["Admin"], ct)).StatusCode);

        // Added to the Admin role from the role's member list.
        var added = await CreateAsync(client, "grant-role", [], reader, ct);
        int adminRoleId;
        await using (var db = Db())
            adminRoleId = await db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).SingleAsync(ct);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync($"/api/roles/{adminRoleId}/users", new { userId = added.Id }, ct)).StatusCode);

        // Losing the role sends nothing.
        Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(client, edited, [], ct)).StatusCode);

        var granted = sender.Sent.Where(m => m.Subject == GrantedSubject).Select(m => m.To).ToList();
        Assert.Equal([created.Email, edited.Email, added.Email], granted);
        await using (var db = Db())
            foreach (var id in new[] { created.Id, edited.Id, added.Id })
            {
                Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "SiteAdminGranted" && a.EntityId == id, ct));
                Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "SiteAdminMailSent" && a.EntityId == id, ct));
            }
    }

    [Fact]
    public async Task WhenTheGrantMailCannotLeave_TheRoleStillChanges_AndTheAuditSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = new CapturingSender(EmailSendResult.Failed("554 relay refused"));
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithSender(baseFactory, sender);
        using var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, ct));
        var user = await CreateAsync(client, "grant-fail", [], await IntegrationUsers.InDefaultOrganizationAsync(client, "Reader"), ct);

        Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(client, user, ["Admin"], ct)).StatusCode);

        Assert.Single(sender.Sent, m => m.Subject == GrantedSubject);
        await using var db = Db();
        Assert.True(await IsAdminAsync(db, user.Email, ct));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "SiteAdminGranted" && a.EntityId == user.Id, ct));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "SiteAdminMailNotSent" && a.EntityId == user.Id, ct));
    }

    private sealed record CreatedUser(int Id, string Username, string Email);

    private static async Task<CreatedUser> CreateAsync(
        HttpClient client, string prefix, List<string> roles, List<OrganizationRoleAssignmentDto> organizationRoles, CancellationToken ct)
    {
        var username = $"{prefix}-{Guid.NewGuid():N}"[..30];
        var email = $"{username}@example.org";
        var response = await client.PostAsJsonAsync("/api/users", new { username, email, roles, organizationRoles }, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, ct))!.Id;
        return new CreatedUser(id, username, email);
    }

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, CreatedUser user, List<string> roles, CancellationToken ct) =>
        client.PutAsJsonAsync($"/api/users/{user.Id}",
            new { username = user.Username, email = user.Email, isActive = true, mustChangePassword = false, roles }, ct);

    private async Task ForgetEveryAdministratorAndTheLockAsync(CancellationToken ct)
    {
        await using var db = Db();
        await db.AppSettings.Where(s => s.Key == MarkerKey).ExecuteDeleteAsync(ct);
        await db.UserRoles.Where(ur => ur.Role.Name == "Admin").ExecuteDeleteAsync(ct);
    }

    private static Task<bool> IsAdminAsync(AppDbContext db, string email, CancellationToken ct) =>
        db.UserRoles.AnyAsync(ur => ur.User.Email == email && ur.Role.Name == "Admin", ct);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The queued forgotten-password mail did not leave within 15 seconds.");
            await Task.Delay(100, ct);
        }
    }

    private static WebApplicationFactory<Program> WithSender(
        AetheusWebApplicationFactory factory, IEmailSender sender, string? adminEmail = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            if (adminEmail is not null) builder.UseSetting("Auth:AdminEmail", adminEmail);
            builder.ConfigureTestServices(services => services.AddSingleton(sender));
        });

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
