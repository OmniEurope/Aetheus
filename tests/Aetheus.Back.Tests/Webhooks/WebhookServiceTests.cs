// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Components.Webhooks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class WebhookServiceTests
{
    private readonly IWebhookRepository _repo = Substitute.For<IWebhookRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly WebhookService _sut;

    public WebhookServiceTests()
    {
        _encryption.EncryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _encryption.DecryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

        _sut = new WebhookService(
            _repo, _audit,
            Substitute.For<IHttpClientFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowPrivateTargets"] = "true" }).Build(),
            Substitute.For<ILogger<WebhookService>>(),
            _encryption,
            TimeProvider.System);
    }

    [Fact]
    public async Task GetSubscriptionsAsync_ReturnsMappedList()
    {
        _repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new WebhookSubscription
            {
                Id = 1, EventType = "pipeline.completed",
                TargetUrl = "https://example.com/hook", IsEnabled = true
            }]);

        var result = await _sut.GetSubscriptionsAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("pipeline.completed", result[0].EventType);
        Assert.True(result[0].IsEnabled);
    }

    [Fact]
    public async Task GetSubscriptionAsync_Found_ReturnsDto()
    {
        _repo.FindAsync(1, Arg.Any<CancellationToken>())
            .Returns(new WebhookSubscription
            {
                Id = 1,
                EventType = "server.offline",
                TargetUrl = "https://example.com",
                Secret = "my-secret"
            });

        var result = await _sut.GetSubscriptionAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("server.offline", result.EventType);
        Assert.True(result.HasSecret);
    }

    [Fact]
    public async Task GetSubscriptionAsync_NoSecret_HasSecretFalse()
    {
        _repo.FindAsync(1, Arg.Any<CancellationToken>())
            .Returns(new WebhookSubscription
            {
                Id = 1,
                EventType = "test",
                TargetUrl = "https://example.com"
            });

        var result = await _sut.GetSubscriptionAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.HasSecret);
    }

    [Fact]
    public async Task GetSubscriptionAsync_NotFound_ReturnsNull()
    {
        _repo.FindAsync(99, Arg.Any<CancellationToken>())
            .Returns((WebhookSubscription?)null);

        Assert.Null(await _sut.GetSubscriptionAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateSubscriptionAsync_CreatesAndReturnsDto()
    {
        _repo.AddAsync(Arg.Any<WebhookSubscription>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.CreateSubscriptionAsync(new CreateWebhookSubscriptionRequest
        {
            EventType = "pipeline.started",
            TargetUrl = "https://hooks.example.com/pipeline"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("pipeline.started", result.EventType);
        await _repo.Received(1).AddAsync(Arg.Any<WebhookSubscription>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Created", "WebhookSubscription", Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSubscriptionAsync_NotFound_ReturnsNull()
    {
        _repo.FindAsync(99, Arg.Any<CancellationToken>())
            .Returns((WebhookSubscription?)null);

        Assert.Null(await _sut.UpdateSubscriptionAsync(99, new UpdateWebhookSubscriptionRequest
        {
            EventType = "x",
            TargetUrl = "https://a.com"
        }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateSubscriptionAsync_Found_UpdatesAndReturns()
    {
        _repo.FindAsync(1, Arg.Any<CancellationToken>())
            .Returns(new WebhookSubscription { Id = 1, EventType = "old", TargetUrl = "https://old.com" });
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.UpdateSubscriptionAsync(1, new UpdateWebhookSubscriptionRequest
        {
            EventType = "updated",
            TargetUrl = "https://new.com",
            IsEnabled = false
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("updated", result.EventType);
        Assert.False(result.IsEnabled);
        await _audit.Received(1).LogAsync("Updated", "WebhookSubscription", 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteSubscriptionAsync_NotFound_ReturnsFalse()
    {
        _repo.FindAsync(99, Arg.Any<CancellationToken>())
            .Returns((WebhookSubscription?)null);

        Assert.False(await _sut.DeleteSubscriptionAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteSubscriptionAsync_Found_DeletesAndReturnsTrue()
    {
        _repo.FindAsync(1, Arg.Any<CancellationToken>())
            .Returns(new WebhookSubscription { Id = 1, EventType = "hook", TargetUrl = "https://a.com" });
        _repo.RemoveAsync(Arg.Any<WebhookSubscription>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Assert.True(await _sut.DeleteSubscriptionAsync(1, ct: TestContext.Current.CancellationToken));
        await _audit.Received(1).LogAsync("Deleted", "WebhookSubscription", 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- FireEventAsync ---

    [Fact]
    public async Task FireEventAsync_NoSubscriptions_DoesNotCallHttp()
    {
        _repo.GetEnabledByEventAsync("test", Arg.Any<CancellationToken>()).Returns([]);
        await _sut.FireEventAsync("test", new { }, ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FireEventAsync_SuccessfulDelivery_ResetsFailureCount()
    {
        var sub = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = "http://test/hook", FailureCount = 3, IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([sub]);

        var httpFactory = Substitute.For<IHttpClientFactory>();
        var handler = new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var sut = new WebhookService(_repo, _audit, httpFactory, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowPrivateTargets"] = "true" }).Build(), Substitute.For<ILogger<WebhookService>>(), _encryption, TimeProvider.System);
        await sut.FireEventAsync("e", new { Data = "test" }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, sub.FailureCount);
        Assert.NotNull(sub.LastTriggeredAt);
    }

    [Fact]
    public async Task FireEventAsync_PermanentFailure_4xx_IncrementsFailureCount()
    {
        var sub = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = "http://test/hook", FailureCount = 0, IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([sub]);

        var httpFactory = Substitute.For<IHttpClientFactory>();
        var handler = new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var sut = new WebhookService(_repo, _audit, httpFactory, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowPrivateTargets"] = "true" }).Build(), Substitute.For<ILogger<WebhookService>>(), _encryption, TimeProvider.System);
        await sut.FireEventAsync("e", new { }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, sub.FailureCount);
    }

    [Fact]
    public async Task FireEventAsync_TransientFailure_5xx_DoesNotIncrementFailureCount()
    {
        var sub = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = "http://test/hook", FailureCount = 0, IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([sub]);

        var httpFactory = Substitute.For<IHttpClientFactory>();
        var handler = new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var sut = new WebhookService(_repo, _audit, httpFactory, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowPrivateTargets"] = "true" }).Build(), Substitute.For<ILogger<WebhookService>>(), _encryption, TimeProvider.System);
        await sut.FireEventAsync("e", new { }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, sub.FailureCount);
    }

    [Fact]
    public async Task FireEventAsync_ExceedsMaxFailures_DisablesWebhook()
    {
        var sub = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = "http://test/hook", FailureCount = 9, IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([sub]);

        var httpFactory = Substitute.For<IHttpClientFactory>();
        // 4xx is permanent and increments the counter.
        var handler = new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest));
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var sut = new WebhookService(_repo, _audit, httpFactory, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowPrivateTargets"] = "true" }).Build(), Substitute.For<ILogger<WebhookService>>(), _encryption, TimeProvider.System);
        await sut.FireEventAsync("e", new { }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(10, sub.FailureCount);
        Assert.False(sub.IsEnabled);
    }

    [Fact]
    public async Task FireEventAsync_WithSecret_AddsSignatureHeader()
    {
        var sub = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = "http://test/hook", Secret = "mySecret", IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([sub]);

        HttpRequestMessage? capturedRequest = null;
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var handler = new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK), req => capturedRequest = req);
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var sut = new WebhookService(_repo, _audit, httpFactory, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowPrivateTargets"] = "true" }).Build(), Substitute.For<ILogger<WebhookService>>(), _encryption, TimeProvider.System);
        await sut.FireEventAsync("e", new { X = 1 }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.Contains("X-Aetheus-Signature"));
        var sig = capturedRequest.Headers.GetValues("X-Aetheus-Signature").First();
        Assert.StartsWith("sha256=", sig);
    }

    [Fact]
    public async Task FireEventAsync_HttpException_DoesNotIncrementFailureCount()
    {
        // HttpRequestException is treated as a transient network error - do NOT count toward auto-disable.
        var sub = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = "http://test/hook", FailureCount = 0, IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([sub]);

        var httpFactory = Substitute.For<IHttpClientFactory>();
        var handler = new FakeHandler(throwOnSend: true);
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var sut = new WebhookService(_repo, _audit, httpFactory, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowPrivateTargets"] = "true" }).Build(), Substitute.For<ILogger<WebhookService>>(), _encryption, TimeProvider.System);
        await sut.FireEventAsync("e", new { }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, sub.FailureCount);
    }

    [Fact]
    public async Task FireEventAsync_AddsEventTypeHeader()
    {
        var sub = new WebhookSubscription { Id = 1, EventType = "pipeline.completed", TargetUrl = "http://test/hook", IsEnabled = true };
        _repo.GetEnabledByEventAsync("pipeline.completed", Arg.Any<CancellationToken>()).Returns([sub]);

        HttpRequestMessage? capturedRequest = null;
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var handler = new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK), req => capturedRequest = req);
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var sut = new WebhookService(_repo, _audit, httpFactory, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowPrivateTargets"] = "true" }).Build(), Substitute.For<ILogger<WebhookService>>(), _encryption, TimeProvider.System);
        await sut.FireEventAsync("pipeline.completed", new { PipelineId = 5 }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.Contains("X-Aetheus-Event"));
        Assert.Equal("pipeline.completed", capturedRequest.Headers.GetValues("X-Aetheus-Event").First());
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://hooks.example.com/hook")]
    [InlineData("http://internal.example.com/hook")]
    [InlineData("http://10.0.0.5/hook")]
    public async Task FireEventAsync_PrivateTargetsRefused_SkipsAnUnsafeTargetAndCountsAFailure(string targetUrl)
    {
        var sub = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = targetUrl, FailureCount = 9, IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([sub]);
        var httpFactory = Substitute.For<IHttpClientFactory>();

        await PrivateTargetsRefused(httpFactory).FireEventAsync("e", new { }, ct: TestContext.Current.CancellationToken);

        httpFactory.DidNotReceive().CreateClient(Arg.Any<string>());
        Assert.Equal(10, sub.FailureCount);
        Assert.False(sub.IsEnabled);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FireEventAsync_PrivateTargetsRefused_DeliversToANameResolvingToPublicAddresses()
    {
        var sub = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = "https://public.example.com/hook", IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([sub]);
        var httpFactory = Substitute.For<IHttpClientFactory>();
        HttpRequestMessage? sent = null;
        httpFactory.CreateClient("webhooks").Returns(new HttpClient(
            new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK), request => sent = request)));

        await PrivateTargetsRefused(httpFactory).FireEventAsync("e", new { }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("https://public.example.com/hook", sent?.RequestUri?.ToString());
        Assert.Equal(0, sub.FailureCount);
    }

    [Fact]
    public async Task FireEventAsync_AnUndecryptableSecret_SkipsThatDeliveryOnly()
    {
        var broken = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = "http://test/broken", Secret = "rotated", FailureCount = 9, IsEnabled = true };
        var healthy = new WebhookSubscription { Id = 2, EventType = "e", TargetUrl = "http://test/healthy", IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([broken, healthy]);
        _encryption.DecryptValue("rotated").Returns(_ => throw new System.Security.Cryptography.CryptographicException("bad key"));
        var sent = new List<string>();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("webhooks").Returns(_ => new HttpClient(
            new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK), request => sent.Add(request.RequestUri!.ToString()))));

        await PrivateTargetsAllowed(httpFactory).FireEventAsync("e", new { }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["http://test/healthy"], sent);
        Assert.Equal(10, broken.FailureCount);
        Assert.False(broken.IsEnabled);
        Assert.Null(broken.LastTriggeredAt);
        Assert.NotNull(healthy.LastTriggeredAt);
    }

    [Fact]
    public async Task FireEventAsync_ATimeout_IsTransient()
    {
        var sub = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = "http://test/slow", FailureCount = 2, IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([sub]);
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("webhooks").Returns(new HttpClient(new TimingOutHandler()));

        await PrivateTargetsAllowed(httpFactory).FireEventAsync("e", new { }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, sub.FailureCount);
        Assert.True(sub.IsEnabled);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.RequestTimeout)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
    public async Task FireEventAsync_RequestTimeoutOrTooManyRequests_IsTransient(System.Net.HttpStatusCode status)
    {
        var sub = new WebhookSubscription { Id = 1, EventType = "e", TargetUrl = "http://test/busy", FailureCount = 2, IsEnabled = true };
        _repo.GetEnabledByEventAsync("e", Arg.Any<CancellationToken>()).Returns([sub]);
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("webhooks").Returns(new HttpClient(new FakeHandler(new HttpResponseMessage(status))));

        await PrivateTargetsAllowed(httpFactory).FireEventAsync("e", new { }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, sub.FailureCount);
        Assert.NotNull(sub.LastTriggeredAt);
    }

    private WebhookService PrivateTargetsAllowed(IHttpClientFactory httpFactory) =>
        new(_repo, _audit, httpFactory,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowPrivateTargets"] = "true" }).Build(),
            Substitute.For<ILogger<WebhookService>>(), _encryption, TimeProvider.System);

    private sealed class TimingOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("The request timed out.");
    }

    // Private targets refused, and a resolver that answers without DNS: internal.example.com is private.
    private WebhookService PrivateTargetsRefused(IHttpClientFactory httpFactory) =>
        new(_repo, _audit, httpFactory, new ConfigurationBuilder().Build(), Substitute.For<ILogger<WebhookService>>(),
            _encryption, TimeProvider.System,
            new PublicHostCheck((host, _) => Task.FromResult(new[]
            {
                System.Net.IPAddress.Parse(host == "internal.example.com" ? "192.168.1.10" : "93.184.216.34")
            })));

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Action<HttpRequestMessage>? _onSend;
        private readonly bool _throwOnSend;

        public FakeHandler(HttpResponseMessage response, Action<HttpRequestMessage>? onSend = null)
        {
            _response = response;
            _onSend = onSend;
        }

        public FakeHandler(bool throwOnSend = false)
        {
            _throwOnSend = throwOnSend;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _onSend?.Invoke(request);
            if (_throwOnSend) throw new HttpRequestException("Network error");
            return Task.FromResult(_response!);
        }
    }
}
