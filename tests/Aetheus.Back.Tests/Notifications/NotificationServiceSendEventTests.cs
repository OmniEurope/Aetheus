// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class NotificationServiceSendEventTests
{
    private readonly INotificationRepository _repo = Substitute.For<INotificationRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
    private readonly Aetheus.Back.Services.IEncryptionService _encryption =
        Substitute.For<Aetheus.Back.Services.IEncryptionService>();
    private readonly INotificationEmailer _emailer = Substitute.For<INotificationEmailer>();
    private readonly NotificationService _sut;

    public NotificationServiceSendEventTests()
    {
        _encryption.EncryptValue(Arg.Any<string>()).Returns(call => "enc:" + call.Arg<string>());
        _encryption.DecryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>()["enc:".Length..]);
        _sut = new NotificationService(
            _repo, _audit,
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(),
            _httpFactory,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NotificationService>>(),
            _encryption,
            TimeProvider.System,
            Substitute.For<IUserNotificationService>(),
            _emailer);
    }

    [Fact]
    public async Task SendEventAsync_NoRules_DoesNothing()
    {
        _repo.GetRulesForEventAsync("test.event", Arg.Any<CancellationToken>())
            .Returns([]);

        await _sut.SendEventAsync("test.event", new { Data = "value" }, ct: TestContext.Current.CancellationToken);

        _httpFactory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Fact]
    public async Task SendEventAsync_EmailChannel_MailsTheActiveAdministrators()
    {
        var channel = new NotificationChannel
        {
            Id = 1,
            Name = "email",
            Type = NotificationChannelType.Email,
            ConfigurationJson = "{}",
            Rules = []
        };
        var rule = new NotificationRule
        {
            Id = 1,
            EventType = "test",
            Channel = channel,
            NotificationChannelId = 1
        };
        _repo.GetRulesForEventAsync("test", Arg.Any<CancellationToken>())
            .Returns([rule]);
        _repo.GetActiveAdministratorEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(["admin@example.org", "ops@example.org"]);
        _emailer.SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>())
            .Returns([EmailSendResult.Sent("no-reply@example.org"), EmailSendResult.Failed("550 5.1.1 mailbox unavailable")]);

        await _sut.SendEventAsync("test", new { }, ct: TestContext.Current.CancellationToken);

        _httpFactory.DidNotReceive().CreateClient(Arg.Any<string>());
        await _emailer.Received(1).SendAsync(
            Arg.Is<IReadOnlyList<NotificationEmail>>(emails =>
                emails.Count == 2
                && emails[0].To == "admin@example.org"
                && emails[1].To == "ops@example.org"
                && emails.All(email => email.EventType == "test")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendEventAsync_EmailChannel_NoAdministratorAddress_SendsNothing()
    {
        var channel = new NotificationChannel { Id = 1, Name = "email", Type = NotificationChannelType.Email, Rules = [] };
        _repo.GetRulesForEventAsync("test", Arg.Any<CancellationToken>())
            .Returns([new NotificationRule { Id = 1, EventType = "test", Channel = channel, NotificationChannelId = 1 }]);
        _repo.GetActiveAdministratorEmailsAsync(Arg.Any<CancellationToken>()).Returns(new List<string>());

        await _sut.SendEventAsync("test", new { }, ct: TestContext.Current.CancellationToken);

        await _emailer.DidNotReceive().SendAsync(Arg.Any<IReadOnlyList<NotificationEmail>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendEventAsync_AiDispatchFailure_DoesNotBlockNormalChannel()
    {
        var dispatcher = Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>();
        // The observer dispatch itself is what isolates a failing subscriber, so the stub throws from
        // the dispatch the service now calls.
        dispatcher.DispatchAsync(
                Arg.Any<Aetheus.Back.Components.Notifications.Events.NotificationEventRaisedEvent>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("AI store unavailable")));
        var handler = new FakeHandler();
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(
            new HttpClient(handler) { BaseAddress = new Uri("http://93.184.216.34/") });
        _repo.GetRulesForEventAsync("deploy", Arg.Any<CancellationToken>()).Returns(
        [
            new NotificationRule
            {
                Id = 1,
                EventType = "deploy",
                NotificationChannelId = 1,
                Channel = new NotificationChannel
                {
                    Id = 1,
                    Name = "slack",
                    Type = NotificationChannelType.Slack,
                    ConfigurationJson = """{"webhookUrl":"http://93.184.216.34/slack"}"""
                }
            }
        ]);
        var service = new NotificationService(
            _repo,
            _audit,
            dispatcher,
            _httpFactory,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NotificationService>>(),
            _encryption,
            TimeProvider.System,
            Substitute.For<IUserNotificationService>(),
            Substitute.For<INotificationEmailer>());

        await service.SendEventAsync(
            "deploy",
            new { Version = "1.0" },
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(
            "http://93.184.216.34/slack",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SendEventAsync_SlackChannel_PostsToWebhookUrl()
    {
        var handler = new FakeHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://93.184.216.34/") };
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var channel = new NotificationChannel
        {
            Id = 1,
            Name = "slack",
            Type = NotificationChannelType.Slack,
            ConfigurationJson = """enc:{"webhookUrl":"http://93.184.216.34/slack"}""",
            Rules = []
        };
        var rule = new NotificationRule
        {
            Id = 1,
            EventType = "deploy",
            Channel = channel,
            NotificationChannelId = 1
        };
        _repo.GetRulesForEventAsync("deploy", Arg.Any<CancellationToken>())
            .Returns([rule]);

        await _sut.SendEventAsync("deploy", new { Version = "1.0" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("http://93.184.216.34/slack", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SendEventAsync_TeamsChannel_PostsToWebhookUrl()
    {
        var handler = new FakeHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://93.184.216.34/") };
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var channel = new NotificationChannel
        {
            Id = 1,
            Name = "teams",
            Type = NotificationChannelType.Teams,
            ConfigurationJson = """{"webhookUrl":"http://93.184.216.34/teams"}""",
            Rules = []
        };
        var rule = new NotificationRule
        {
            Id = 1,
            EventType = "build",
            Channel = channel,
            NotificationChannelId = 1
        };
        _repo.GetRulesForEventAsync("build", Arg.Any<CancellationToken>())
            .Returns([rule]);

        await _sut.SendEventAsync("build", new { Status = "success" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("http://93.184.216.34/teams", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SendEventAsync_WebhookChannel_PostsWithHeaders()
    {
        var handler = new FakeHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://93.184.216.34/") };
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var channel = new NotificationChannel
        {
            Id = 1,
            Name = "hook",
            Type = NotificationChannelType.Webhook,
            ConfigurationJson = """{"url":"http://93.184.216.34/hook","secret":"mysecret"}""",
            Rules = []
        };
        var rule = new NotificationRule
        {
            Id = 1,
            EventType = "alert",
            Channel = channel,
            NotificationChannelId = 1
        };
        _repo.GetRulesForEventAsync("alert", Arg.Any<CancellationToken>())
            .Returns([rule]);

        await _sut.SendEventAsync("alert", new { Level = "critical" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("X-Aetheus-Event"));
        Assert.True(handler.LastRequest.Headers.Contains("X-Aetheus-Signature"));
    }

    [Fact]
    public async Task SendEventAsync_WebhookChannelNoSecret_NoSignatureHeader()
    {
        var handler = new FakeHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://93.184.216.34/") };
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var channel = new NotificationChannel
        {
            Id = 1,
            Name = "hook",
            Type = NotificationChannelType.Webhook,
            ConfigurationJson = """{"url":"http://93.184.216.34/hook"}""",
            Rules = []
        };
        var rule = new NotificationRule
        {
            Id = 1,
            EventType = "evt",
            Channel = channel,
            NotificationChannelId = 1
        };
        _repo.GetRulesForEventAsync("evt", Arg.Any<CancellationToken>())
            .Returns([rule]);

        await _sut.SendEventAsync("evt", new { }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("X-Aetheus-Event"));
        Assert.False(handler.LastRequest.Headers.Contains("X-Aetheus-Signature"));
    }

    [Fact]
    public async Task SendEventAsync_SlackNoWebhookUrl_DoesNotPost()
    {
        var channel = new NotificationChannel
        {
            Id = 1,
            Name = "slack-no-url",
            Type = NotificationChannelType.Slack,
            ConfigurationJson = """{"other":"value"}""",
            Rules = []
        };
        var rule = new NotificationRule
        {
            Id = 1,
            EventType = "test",
            Channel = channel,
            NotificationChannelId = 1
        };
        _repo.GetRulesForEventAsync("test", Arg.Any<CancellationToken>())
            .Returns([rule]);

        await _sut.SendEventAsync("test", new { }, ct: TestContext.Current.CancellationToken);

        _httpFactory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Fact]
    public async Task UpdateRuleAsync_Found_UpdatesAndReturns()
    {
        var rule = new NotificationRule
        {
            Id = 1,
            EventType = "old",
            NotificationChannelId = 1
        };
        _repo.FindRuleAsync(1, Arg.Any<CancellationToken>()).Returns(rule);

        var result = await _sut.UpdateRuleAsync(1, new Aetheus.Shared.Components.Notifications.UpdateNotificationRuleRequest
        {
            EventType = "new.event",
            FilterJson = "{\"filter\":true}",
            IsEnabled = false
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("new.event", result!.EventType);
    }

    [Fact]
    public async Task UpdateRuleAsync_NotFound_ReturnsNull()
    {
        _repo.FindRuleAsync(99, Arg.Any<CancellationToken>())
            .Returns((NotificationRule?)null);

        Assert.Null(await _sut.UpdateRuleAsync(99, new Aetheus.Shared.Components.Notifications.UpdateNotificationRuleRequest(), ct: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("http://hooks.example.com/slack", true)]
    [InlineData("https://hooks.example.com/slack", true)]
    [InlineData("ftp://hooks.example.com/slack", false)]
    [InlineData("/relative/slack", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void HttpTarget_AcceptsOnlyAbsoluteHttpUrls(string? url, bool accepted) =>
        Assert.Equal(accepted, NotificationService.HttpTarget(url) is not null);

    [Theory]
    [InlineData("public.example.com", true)]
    [InlineData("internal.example.com", false)]
    public async Task SendEventAsync_SlackChannel_PostsOnlyToAHostResolvingToPublicAddresses(string host, bool posted)
    {
        var handler = new FakeHandler();
        _httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        var channel = new NotificationChannel
        {
            Id = 1,
            Name = "slack",
            Type = NotificationChannelType.Slack,
            ConfigurationJson = $$"""enc:{"webhookUrl":"https://{{host}}/slack"}""",
            Rules = []
        };
        _repo.GetRulesForEventAsync("deploy", Arg.Any<CancellationToken>())
            .Returns([new NotificationRule { Id = 1, EventType = "deploy", Channel = channel, NotificationChannelId = 1 }]);
        // A resolver that answers without DNS: internal.example.com is private.
        var service = new NotificationService(
            _repo, _audit, Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(), _httpFactory,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<NotificationService>>(), _encryption, TimeProvider.System,
            Substitute.For<IUserNotificationService>(), _emailer,
            new Aetheus.Back.Components.Shared.PublicHostCheck((name, _) => Task.FromResult(new[]
            {
                System.Net.IPAddress.Parse(name == "internal.example.com" ? "10.0.0.7" : "93.184.216.34")
            })));

        await service.SendEventAsync("deploy", new { Version = "1.0" }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(posted ? $"https://{host}/slack" : null, handler.LastRequest?.RequestUri?.ToString());
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
