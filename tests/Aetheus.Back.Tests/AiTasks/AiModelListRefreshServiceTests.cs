// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.AiTasks;

/// <summary>
/// Recette R3-7: the weekly read (night window) of each API profile's model list. Night window and once-a-week
/// selection, the list stored on success, the previous list kept and the masked reason stored on a
/// failure, unsupported providers left alone, the loop and its leader lease. HTTP is a fake handler.
/// </summary>
public sealed class AiModelListRefreshServiceTests
{
    private const string Key = "sk-ant-secret-0123456789";
    private static readonly DateTimeOffset Night = new(2026, 10, 13, 1, 30, 0, TimeSpan.Zero);

    // Replaced, not moved back, by the tests that start earlier: a fake clock only goes forward.
    private FakeTimeProvider _clock = new(Night);
    private readonly IAiTaskRepository _repo = Substitute.For<IAiTaskRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
    private readonly RecordingLogger<AiModelListRefreshService> _logger = new();
    private readonly RecordingLogger<AiModelListReader> _readerLogger = new();
    private readonly List<(string Uri, string? Key)> _sent = [];
    private Func<HttpResponseMessage> _answer = () => Json(HttpStatusCode.OK, """{"data":[{"id":"claude-opus-5-5"},{"id":"claude-haiku-5"}]}""");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AiModelListRefreshServiceTests()
    {
        _encryption.DecryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>().Replace("enc:", string.Empty, StringComparison.Ordinal));
        _httpFactory.CreateClient(AiModelListReader.HttpClientName).Returns(_ => new HttpClient(new Handler(this)));
        Due();
    }

    private AiModelListRefreshService Sut(IPostgresLeaderLease? lease = null) => new(
        BackgroundServiceTestKit.ScopeFactory((typeof(IAiTaskRepository), _repo), (typeof(IEncryptionService), _encryption)),
        new AiModelListReader(_httpFactory, _readerLogger), _clock, _logger, lease);

    private void Due(params AiRunnerProfile[] profiles) =>
        _repo.GetProfilesDueForModelListAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(profiles.ToList());

    private static AiRunnerProfile Profile(int id, string provider = "anthropic", string baseUrl = "") => new()
    {
        Id = id,
        Name = $"p{id}",
        Provider = provider,
        BaseUrl = baseUrl,
        Model = "claude-sonnet-5-5",
        ApiKeyEncrypted = "enc:" + Key
    };

    [Fact]
    public async Task R3_7_AtNight_ReadsTheDueProfiles_StoresTheSortedList_AndSavesEach()
    {
        var anthropic = Profile(1);
        anthropic.ModelListError = "old failure";
        var openAi = Profile(2, "openai", "https://llm.example.com/v1");
        Due(anthropic, openAi);

        var read = await Sut().RefreshAsync(Ct);

        Assert.Equal(2, read);
        await _repo.Received(1).GetProfilesDueForModelListAsync(Night.UtcDateTime.AddHours(-156), Arg.Any<CancellationToken>());
        Assert.Equal("""["claude-haiku-5","claude-opus-5-5"]""", anthropic.AvailableModelsJson);
        Assert.Equal(Night.UtcDateTime, anthropic.AvailableModelsFetchedAt);
        Assert.Equal(Night.UtcDateTime, anthropic.ModelListCheckedAt);
        Assert.Null(anthropic.ModelListError);
        Assert.Equal(
            [("https://api.anthropic.com/v1/models?limit=1000", Key), ("https://llm.example.com/v1/models", null)],
            _sent);
        await _repo.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Contains(_logger.Records, entry => entry.Level == LogLevel.Information && entry.Message.Contains("2 of 2"));
    }

    [Fact]
    public async Task R3_7_AProfile_IsReadAboutOnceAWeek_AtTheSameNightEachWeek()
    {
        var profile = Profile(1);
        // Same selection as the repository: never attempted, or attempted before the cut-off.
        _repo.GetProfilesDueForModelListAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(profile.ModelListCheckedAt is null || profile.ModelListCheckedAt < call.Arg<DateTime>()
                ? new List<AiRunnerProfile> { profile }
                : []));

        Assert.Equal(1, await Sut().RefreshAsync(Ct)); // first night: never attempted
        var first = profile.ModelListCheckedAt;
        Assert.Equal(Night.UtcDateTime, first);

        for (var day = 1; day <= 6; day++)
        {
            _clock.Advance(TimeSpan.FromDays(1));
            Assert.Equal(0, await Sut().RefreshAsync(Ct)); // nights 2 to 7: not read again
        }
        Assert.Equal(first, profile.ModelListCheckedAt);

        _clock.Advance(TimeSpan.FromDays(1)); // night 8, exactly one week later
        Assert.Equal(1, await Sut().RefreshAsync(Ct));
        Assert.Equal(Night.UtcDateTime.AddDays(7), profile.ModelListCheckedAt);
        Assert.Equal(TimeSpan.FromDays(6.5), AiModelListRefreshService.RecheckAfter);
    }

    [Fact]
    public async Task R3_7_ProviderError_KeepsThePreviousList_StoresTheMaskedReason_AndNeverLogsTheKey()
    {
        var profile = Profile(1);
        profile.AvailableModelsJson = """["claude-old"]""";
        profile.AvailableModelsFetchedAt = Night.UtcDateTime.AddDays(-1);
        Due(profile);
        _answer = () => Json(HttpStatusCode.Unauthorized, $$$"""{"error":{"message":"invalid x-api-key {{{Key}}}"}}""");

        var read = await Sut().RefreshAsync(Ct);

        Assert.Equal(0, read);
        Assert.Equal("""["claude-old"]""", profile.AvailableModelsJson);
        Assert.Equal(Night.UtcDateTime.AddDays(-1), profile.AvailableModelsFetchedAt);
        Assert.Equal(Night.UtcDateTime, profile.ModelListCheckedAt);
        Assert.Contains("HTTP 401", profile.ModelListError, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, profile.ModelListError, StringComparison.Ordinal);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        var warning = Assert.Single(_readerLogger.Records, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("profile 1", warning.Message, StringComparison.Ordinal);
        Assert.All(_logger.Records.Select(entry => entry.Message).Concat(_readerLogger.Records.Select(entry => entry.Message)), message => Assert.DoesNotContain(Key, message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task R3_7_AKeyThatNoLongerDecrypts_IsThatProfilesFailure_AndTheOthersAreStillRead()
    {
        var broken = Profile(1);
        broken.ApiKeyEncrypted = "enc:broken";
        var healthy = Profile(2);
        Due(broken, healthy);
        _encryption.DecryptValue("enc:broken").Throws(new System.Security.Cryptography.CryptographicException("bad padding"));

        var read = await Sut().RefreshAsync(Ct);

        Assert.Equal(1, read);
        Assert.Equal("CryptographicException: bad padding", broken.ModelListError);
        Assert.Null(broken.AvailableModelsJson);
        Assert.NotNull(healthy.AvailableModelsJson);
        Assert.Single(_sent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("claude-cli")]
    public async Task R3_7_UnsupportedProvider_SendsNothing_AndKeepsTheFixedList(string provider)
    {
        var profile = Profile(1, provider);
        Due(profile);

        var read = await Sut().RefreshAsync(Ct);

        Assert.Equal(0, read);
        Assert.Empty(_sent);
        Assert.Null(profile.AvailableModelsJson);
        Assert.Null(profile.ModelListCheckedAt);
        Assert.Null(profile.ModelListError);
        await _repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Theory]
    [InlineData(0, 59, false)]
    [InlineData(1, 0, true)]
    [InlineData(4, 59, true)]
    [InlineData(5, 0, false)]
    [InlineData(14, 0, false)]
    public async Task R3_7_OnlyTheNightWindow_ReadsAnything(int hour, int minute, bool night)
    {
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, hour, minute, 0, TimeSpan.Zero));

        await Sut().RefreshAsync(Ct);

        Assert.Equal(night, AiModelListRefreshService.IsNight(_clock.GetUtcNow().UtcDateTime));
        await _repo.Received(night ? 1 : 0).GetProfilesDueForModelListAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R3_7_Loop_WakesEveryQuarterHour_AndReadsOnlyOnceTheNightStarts()
    {
        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 0, 50, 0, TimeSpan.Zero));
        var passes = new SemaphoreSlim(0);
        _repo.GetProfilesDueForModelListAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                passes.Release();
                return Task.FromResult(new List<AiRunnerProfile>());
            });
        var (lease, started) = BackgroundServiceTestKit.LeaderLease();
        var sut = Sut(lease);

        await sut.StartAsync(Ct);
        await started.AwaitAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(15)); // 01:05, inside the night
        await passes.AwaitAsync(Ct);
        await sut.StopAsync(Ct);

        await _repo.Received(1).GetProfilesDueForModelListAsync(
            new DateTime(2026, 10, 6, 13, 5, 0, DateTimeKind.Utc), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R3_7_WithALease_RunsUnderItsOwnLease_AndNothingWhenNotLeader()
    {
        var (lease, leaseName) = BackgroundServiceTestKit.NonLeaderLease();
        var sut = Sut(lease);

        await sut.StartAsync(Ct);
        Assert.Equal("aetheus:ai-model-list-refresh", await leaseName.NameAsync(Ct));
        _clock.Advance(TimeSpan.FromHours(2));
        await sut.StopAsync(Ct);

        await _repo.DidNotReceiveWithAnyArgs().GetProfilesDueForModelListAsync(default, Ct);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void R3_7_TheNightlyPass_IsRegisteredUnlessTheHostSwitchesItOff(string? enabled, bool registered)
    {
        // Production sets nothing (on); the integration, E2E and QA hosts set the switch to false.
        var settings = new Dictionary<string, string?>();
        if (enabled is not null) settings[AiTasksModuleExtensions.ModelListRefreshEnabledKey] = enabled;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection().AddAiTasksModule(configuration);

        Assert.Equal(registered, services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(AiModelListRefreshService)));
        // The reader stays for the dialog's "read now" either way.
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AiModelListReader));
    }

    [Fact]
    public void R3_7_TheTestHost_DoesNotRunTheNightlyPass()
    {
        using var factory = new CustomWebApplicationFactory();

        Assert.Empty(factory.Services.GetServices<IHostedService>().OfType<AiModelListRefreshService>());
        Assert.NotNull(factory.Services.GetService<AiModelListReader>());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(AiModelListRefreshServiceTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._sent.Add((request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null));
            return Task.FromResult(test._answer());
        }
    }
}
