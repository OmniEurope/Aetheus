// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class NotificationServiceTests
{
    private readonly INotificationRepository _repo = Substitute.For<INotificationRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
    private readonly Aetheus.Back.Services.IEncryptionService _encryption =
        Substitute.For<Aetheus.Back.Services.IEncryptionService>();
    private readonly NotificationService _sut;

    public NotificationServiceTests()
    {
        _encryption.EncryptValue(Arg.Any<string>()).Returns(call => "enc:" + call.Arg<string>());
        _encryption.DecryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>()["enc:".Length..]);
        _sut = new NotificationService(
            _repo, _audit,
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(),
            _httpFactory,
            Substitute.For<ILogger<NotificationService>>(),
            _encryption,
            TimeProvider.System,
            Substitute.For<IUserNotificationService>());
    }

    [Fact]
    public async Task GetRulesAsync_ReturnsMappedList()
    {
        _repo.GetRulesPagedAsync(
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((
                [new NotificationRule { Id = 3, EventType = "alert.triggered", NotificationChannelId = 1, Channel = new NotificationChannel { Id = 1, Name = "slack" } }],
                1));

        var result = await _sut.GetRulesAsync(new PaginationRequest(), ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("alert.triggered", result.Items[0].EventType);
        Assert.Equal("slack", result.Items[0].ChannelName);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task TestChannelAsync_NotFound_ReturnsNull()
    {
        _repo.FindChannelAsync(99, Arg.Any<CancellationToken>()).Returns((NotificationChannel?)null);
        Assert.Null(await _sut.TestChannelAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TestChannelAsync_EmailChannel_ReturnsNotConfigured_NeverSent()
    {
        _repo.FindChannelAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NotificationChannel { Id = 1, Name = "mail", Type = NotificationChannelType.Email, ConfigurationJson = "{}" });

        var result = await _sut.TestChannelAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(NotificationTestStatus.NotConfigured, result!.Status);
    }

    [Fact]
    public async Task TestChannelAsync_WebhookWithoutUrl_ReturnsNotConfigured()
    {
        _repo.FindChannelAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NotificationChannel { Id = 1, Name = "wh", Type = NotificationChannelType.Webhook, ConfigurationJson = "{}" });

        var result = await _sut.TestChannelAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(NotificationTestStatus.NotConfigured, result!.Status);
    }

    [Fact]
    public async Task TestChannelAsync_SlackWithReachableUrl_ReturnsSent()
    {
        // A public IP literal skips DNS resolution in the SSRF check; the stubbed client accepts the POST.
        _repo.FindChannelAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NotificationChannel { Id = 1, Name = "slack", Type = NotificationChannelType.Slack, ConfigurationJson = "{\"webhookUrl\":\"https://93.184.216.34/hook\"}" });
        _httpFactory.CreateClient("webhooks").Returns(new HttpClient(new OkHandler()));

        var result = await _sut.TestChannelAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(NotificationTestStatus.Sent, result!.Status);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    [Theory]
    [InlineData(NotificationChannelType.Slack, "webhookUrl", 401)]
    [InlineData(NotificationChannelType.Slack, "webhookUrl", 429)]
    [InlineData(NotificationChannelType.Slack, "webhookUrl", 500)]
    [InlineData(NotificationChannelType.Teams, "webhookUrl", 401)]
    [InlineData(NotificationChannelType.Teams, "webhookUrl", 429)]
    [InlineData(NotificationChannelType.Teams, "webhookUrl", 500)]
    [InlineData(NotificationChannelType.Webhook, "url", 401)]
    [InlineData(NotificationChannelType.Webhook, "url", 429)]
    [InlineData(NotificationChannelType.Webhook, "url", 500)]
    public async Task TestChannelAsync_ProviderRejectsTheMessage_ReportsFailedNotSent(
        NotificationChannelType type, string urlProperty, int status)
    {
        _repo.FindChannelAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NotificationChannel { Id = 1, Name = "ch", Type = type, ConfigurationJson = $"{{\"{urlProperty}\":\"https://93.184.216.34/hook\"}}" });
        var handler = new StatusHandler((System.Net.HttpStatusCode)status);
        _httpFactory.CreateClient("webhooks").Returns(new HttpClient(handler));

        var result = await _sut.TestChannelAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(NotificationTestStatus.Failed, result!.Status);
        Assert.Contains($"HTTP {status}", result.Message, StringComparison.Ordinal);
        await _audit.DidNotReceive().LogAsync("Tested", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetChannelsAsync_ReturnsMappedList()
    {
        _repo.GetChannelsPagedAsync(
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((
                [new NotificationChannel { Id = 1, Name = "slack-alerts", Type = NotificationChannelType.Slack, Rules = [] }],
                1));

        var result = await _sut.GetChannelsAsync(new PaginationRequest(), ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("slack-alerts", result.Items[0].Name);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task GetChannelAsync_Found_ReturnsDto()
    {
        _repo.GetChannelWithRulesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NotificationChannel
            {
                Id = 1,
                Name = "teams-channel",
                Type = NotificationChannelType.Teams,
                Rules = [new NotificationRule { Id = 1, EventType = "pipeline.completed" }]
            });

        var result = await _sut.GetChannelAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("teams-channel", result.Name);
    }

    [Fact]
    public async Task GetChannelAsync_WebhookConfiguration_MasksUrlAndSecret()
    {
        _repo.GetChannelWithRulesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NotificationChannel
            {
                Id = 1,
                Name = "webhook",
                Type = NotificationChannelType.Webhook,
                ConfigurationJson =
                    """{"url":"https://hooks.example/tenant/key","secret":"signing-secret","format":"json"}""",
                Rules = []
            });

        var result = await _sut.GetChannelAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain("hooks.example", result.ConfigurationJson, StringComparison.Ordinal);
        Assert.DoesNotContain("signing-secret", result.ConfigurationJson, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"***\"", result.ConfigurationJson, StringComparison.Ordinal);
        Assert.Contains("\"secret\":\"***\"", result.ConfigurationJson, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"json\"", result.ConfigurationJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetChannelAsync_NotFound_ReturnsNull()
    {
        _repo.GetChannelWithRulesAsync(99, Arg.Any<CancellationToken>())
            .Returns((NotificationChannel?)null);

        Assert.Null(await _sut.GetChannelAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateChannelAsync_CreatesAndReturnsDto()
    {
        _repo.AddChannelAsync(Arg.Any<NotificationChannel>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.CreateChannelAsync(new CreateNotificationChannelRequest
        {
            Name = "slack",
            Type = NotificationChannelType.Slack,
            ConfigurationJson = "{\"webhook_url\":\"https://hooks.slack.com/test\"}"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("slack", result.Name);
        await _repo.Received(1).AddChannelAsync(
            Arg.Is<NotificationChannel>(channel =>
                channel.ConfigurationJson == "enc:{\"webhook_url\":\"https://hooks.slack.com/test\"}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateChannelAsync_NotFound_ReturnsNull()
    {
        _repo.FindChannelAsync(99, Arg.Any<CancellationToken>())
            .Returns((NotificationChannel?)null);

        Assert.Null(await _sut.UpdateChannelAsync(99, new UpdateNotificationChannelRequest { Name = "x" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateChannelAsync_Found_UpdatesAndReturns()
    {
        _repo.FindChannelAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NotificationChannel { Id = 1, Name = "old", Rules = [] });
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.UpdateChannelAsync(1, new UpdateNotificationChannelRequest
        {
            Name = "updated",
            ConfigurationJson = "{}",
            IsEnabled = false
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("updated", result.Name);
    }

    [Fact]
    public async Task UpdateChannelAsync_MaskedSecrets_PreservesStoredValues()
    {
        var channel = new NotificationChannel
        {
            Id = 1,
            Name = "old",
            Type = NotificationChannelType.Webhook,
            ConfigurationJson =
                """{"url":"https://hooks.example/tenant/key","secret":"signing-secret"}""",
            Rules = []
        };
        _repo.FindChannelAsync(1, Arg.Any<CancellationToken>()).Returns(channel);

        await _sut.UpdateChannelAsync(1, new UpdateNotificationChannelRequest
        {
            Name = "updated",
            ConfigurationJson = """{"url":"***","secret":"***"}""",
            IsEnabled = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(
            "enc:{\"url\":\"https://hooks.example/tenant/key\",\"secret\":\"signing-secret\"}",
            channel.ConfigurationJson);
        _encryption.Received(1).EncryptValue(
            "{\"url\":\"https://hooks.example/tenant/key\",\"secret\":\"signing-secret\"}");
    }

    [Fact]
    public async Task DeleteChannelAsync_NotFound_ReturnsFalse()
    {
        _repo.FindChannelAsync(99, Arg.Any<CancellationToken>())
            .Returns((NotificationChannel?)null);

        Assert.False(await _sut.DeleteChannelAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteChannelAsync_Found_ReturnsTrue()
    {
        _repo.FindChannelAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NotificationChannel { Id = 1, Name = "ch" });
        _repo.RemoveChannelAsync(Arg.Any<NotificationChannel>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Assert.True(await _sut.DeleteChannelAsync(1, ct: TestContext.Current.CancellationToken));
        await _repo.Received(1).RemoveChannelAsync(Arg.Any<NotificationChannel>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRuleAsync_CreatesAndReturnsDto()
    {
        _repo.AddRuleAsync(Arg.Any<NotificationRule>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.CreateRuleAsync(new CreateNotificationRuleRequest
        {
            NotificationChannelId = 1,
            EventType = "server.offline"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("server.offline", result.EventType);
        await _repo.Received(1).AddRuleAsync(Arg.Any<NotificationRule>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRuleAsync_NotFound_ReturnsFalse()
    {
        _repo.FindRuleAsync(99, Arg.Any<CancellationToken>())
            .Returns((NotificationRule?)null);

        Assert.False(await _sut.DeleteRuleAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteRuleAsync_Found_ReturnsTrue()
    {
        _repo.FindRuleAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NotificationRule { Id = 1, EventType = "test" });
        _repo.RemoveRuleAsync(Arg.Any<NotificationRule>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Assert.True(await _sut.DeleteRuleAsync(1, ct: TestContext.Current.CancellationToken));
    }
}
