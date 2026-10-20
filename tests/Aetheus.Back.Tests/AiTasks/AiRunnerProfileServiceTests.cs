// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AiTasks;

/// <summary>
/// API runner profiles: validation (provider, https base URL, model, key), the key encrypted and never
/// returned, kept when an update sends it empty or masked, CLI-era profiles read as "to reconfigure",
/// and the connection test sent from the backend with the stored key.
/// </summary>
public sealed class AiRunnerProfileServiceTests
{
    private const string StoredKey = "sk-stored-0123456789";

    private readonly IAiTaskRepository _repo = Substitute.For<IAiTaskRepository>();
    private readonly IOrganizationRepository _organizations = Substitute.For<IOrganizationRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<AiModelListReader> _readerLogger = new();
    private readonly List<(string Uri, string? Key, string Body)> _sent = [];
    private HttpStatusCode _status = HttpStatusCode.OK;
    private string _answer = """{"content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn"}""";
    private readonly AiRunnerProfileService _sut;

    public AiRunnerProfileServiceTests()
    {
        _encryption.EncryptValue(Arg.Any<string>()).Returns(call => "enc:" + call.Arg<string>());
        _encryption.DecryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>().Replace("enc:", string.Empty, StringComparison.Ordinal));
        _organizations.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        _repo.AddProfileAsync(Arg.Any<AiRunnerProfile>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<AiRunnerProfile>().Id = 41;
                return Task.CompletedTask;
            });
        _httpFactory.CreateClient(AiRunnerProfileService.TestHttpClientName)
            .Returns(_ => new HttpClient(new RecordingHandler(this)));
        _httpFactory.CreateClient(AiModelListReader.HttpClientName)
            .Returns(_ => new HttpClient(new RecordingHandler(this)));
        _sut = new AiRunnerProfileService(
            _repo, _organizations, _audit, _encryption, _httpFactory, new AiModelListReader(_httpFactory, _readerLogger), _clock);
    }

    [Fact]
    public async Task Create_EncryptsTheKey_WritesTheRetiredColumns_AndNeverReturnsTheKey()
    {
        AiRunnerProfile? stored = null;
        _repo.AddProfileAsync(Arg.Do<AiRunnerProfile>(profile => { stored = profile; profile.Id = 41; }), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.CreateProfileAsync(Valid() with
        {
            Name = "  claude-sonnet  ",
            ApiKey = " sk-new-key-123456 ",
            TimeoutSeconds = -10,
            MaxOutputBytes = 99
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal("enc:sk-new-key-123456", stored.ApiKeyEncrypted);
        Assert.Equal("anthropic", stored.Provider);
        Assert.Equal(string.Empty, stored.BaseUrl);
        Assert.Equal("low", stored.ReasoningEffort);
        Assert.Equal(string.Empty, stored.Binary);
        Assert.Equal("[]", stored.ArgsTemplateJson);
        Assert.Equal("enc:{}", stored.EnvironmentJsonEncrypted);
        Assert.Equal("claude-sonnet", result.Name);
        Assert.Equal(7, result.OrganizationId);
        Assert.Equal(1, result.TimeoutSeconds);
        Assert.Equal(1024, result.MaxOutputBytes);
        Assert.True(result.HasApiKey);
        Assert.False(result.NeedsReconfiguration);
        Assert.DoesNotContain("sk-new-key", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        await _audit.Received(1).LogAsync("Created", "AiRunnerProfile", 41, "claude-sonnet", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, "https://api.anthropic.com", "claude-sonnet-5-5", "sk-1")]
    [InlineData(AiProvider.Anthropic, "http://api.anthropic.com", "claude-sonnet-5-5", "sk-1")]
    [InlineData(AiProvider.Anthropic, "https://user:secret@api.anthropic.com", "claude-sonnet-5-5", "sk-1")]
    [InlineData(AiProvider.OpenAi, "", " ", "sk-1")]
    [InlineData(AiProvider.OpenAi, "", "gpt-5.5", "")]
    [InlineData(AiProvider.OpenAi, "", "gpt-5.5", "***")]
    public async Task Create_InvalidProviderUrlModelOrKey_IsRejected(AiProvider? provider, string baseUrl, string model, string key)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateProfileAsync(
            Valid() with { Provider = provider, BaseUrl = baseUrl, Model = model, ApiKey = key },
            TestContext.Current.CancellationToken));
        await _repo.DidNotReceiveWithAnyArgs().AddProfileAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Create_WithoutAName_IsNamedAfterProviderAndModel_WithASuffixWhenTaken()
    {
        AiRunnerProfile? stored = null;
        _repo.AddProfileAsync(Arg.Do<AiRunnerProfile>(profile => stored = profile), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repo.GetProfilesForOrganizationsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns([new AiRunnerProfile { Name = "anthropic-claude-sonnet-5-5" }, new AiRunnerProfile { Name = "Anthropic-Claude-Sonnet-5-5-2" }]);

        await _sut.CreateProfileAsync(
            Valid() with { Name = "  ", Model = "claude-sonnet-5-5" }, TestContext.Current.CancellationToken);

        Assert.Equal("anthropic-claude-sonnet-5-5-3", stored!.Name);
    }

    [Fact]
    public async Task Create_WithTheProviderDefaultUrl_KeepsFollowingTheDefault()
    {
        AiRunnerProfile? stored = null;
        _repo.AddProfileAsync(Arg.Do<AiRunnerProfile>(profile => stored = profile), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.CreateProfileAsync(
            Valid() with { BaseUrl = "https://api.anthropic.com/" }, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, stored!.BaseUrl);
    }

    [Fact]
    public async Task Create_CustomBaseUrl_IsStoredWithoutTrailingSlash()
    {
        AiRunnerProfile? stored = null;
        _repo.AddProfileAsync(Arg.Do<AiRunnerProfile>(profile => stored = profile), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.CreateProfileAsync(
            Valid() with { Provider = AiProvider.OpenAi, BaseUrl = "https://llm.example.com/v1/", Model = "gpt-5.5" },
            TestContext.Current.CancellationToken);

        Assert.Equal("https://llm.example.com/v1", stored!.BaseUrl);
        Assert.Equal("openai", stored.Provider);
    }

    [Theory]
    [InlineData("")]
    [InlineData("***")]
    public async Task Update_EmptyOrMaskedKey_KeepsTheStoredKey(string sentKey)
    {
        var profile = Stored();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);

        var result = await _sut.UpdateProfileAsync(profile.Id, Update() with { ApiKey = sentKey, Model = " claude-opus-5-5 " },
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("enc:" + StoredKey, profile.ApiKeyEncrypted);
        Assert.Equal("claude-opus-5-5", profile.Model);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_NewKey_ReplacesTheStoredKey()
    {
        var profile = Stored();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);

        await _sut.UpdateProfileAsync(profile.Id, Update() with { ApiKey = "sk-rotated-999" }, TestContext.Current.CancellationToken);

        Assert.Equal("enc:sk-rotated-999", profile.ApiKeyEncrypted);
    }

    [Fact]
    public async Task Update_CliProfileWithoutAKey_RequiresOne()
    {
        var profile = CliProfile();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdateProfileAsync(
            profile.Id, Update() with { ApiKey = string.Empty }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_MissingProfile_ReturnsNull()
    {
        _repo.FindProfileAsync(404, Arg.Any<CancellationToken>()).Returns((AiRunnerProfile?)null);

        Assert.Null(await _sut.UpdateProfileAsync(404, Update(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Queries_MapACliProfileAsToReconfigure_WithoutFailing()
    {
        var cli = CliProfile();
        var api = Stored();
        _repo.GetProfilesPageAsync("x", 1, 10, Arg.Any<CancellationToken>()).Returns(([cli, api], 2));

        var page = await _sut.GetProfilesAsync(new PaginationRequest { Search = "x", Page = 1, PageSize = 10 },
            TestContext.Current.CancellationToken);

        Assert.Null(page.Items[0].Provider);
        Assert.True(page.Items[0].NeedsReconfiguration);
        Assert.False(page.Items[0].HasApiKey);
        Assert.Equal(AiProvider.Anthropic, page.Items[1].Provider);
        Assert.Equal(AiReasoningEffort.Low, page.Items[1].ReasoningEffort);
        Assert.False(page.Items[1].NeedsReconfiguration);
        Assert.True(page.Items[1].HasApiKey);
    }

    [Fact]
    public async Task Delete_EnforcesReferences_AndRemovesAnUnusedProfile()
    {
        var profile = Stored();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);
        _repo.ProfileHasDefinitionsAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(true, false);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.DeleteProfileAsync(profile.Id, TestContext.Current.CancellationToken));
        Assert.True(await _sut.DeleteProfileAsync(profile.Id, TestContext.Current.CancellationToken));

        await _repo.Received(1).RemoveProfileAsync(profile, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Deleted", "AiRunnerProfile", profile.Id, profile.Name, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestConnection_WithoutATypedKey_UsesTheStoredOne_AndReturnsNoKey()
    {
        var profile = Stored();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);

        var result = await _sut.TestConnectionAsync(
            Test() with { ProfileId = profile.Id, ApiKey = AiRunnerProfileRequest.KeepStoredApiKey },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        var sent = Assert.Single(_sent);
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Uri);
        Assert.Equal(StoredKey, sent.Key);
        Assert.Contains("claude-sonnet-5-5", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(StoredKey, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        await _audit.Received(1).LogAsync("ConnectionTestSucceeded", "AiRunnerProfile", profile.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://attacker.example.com")]
    [InlineData("https://api.anthropic.com.attacker.example.com")]
    [InlineData("https://api.anthropic.com/v2")]
    public async Task TestConnection_StoredKeyToAnotherBaseUrl_IsRefusedWithoutCallingAnyHost(string baseUrl)
    {
        var profile = Stored();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);

        var error = await Assert.ThrowsAsync<BadRequestException>(() => _sut.TestConnectionAsync(
            Test() with { ProfileId = profile.Id, ApiKey = AiRunnerProfileRequest.KeepStoredApiKey, BaseUrl = baseUrl },
            TestContext.Current.CancellationToken));

        Assert.Contains("base URL of the saved profile", error.Message, StringComparison.Ordinal);
        Assert.Empty(_sent);
        _encryption.DidNotReceive().DecryptValue(Arg.Any<string>());
    }

    [Fact]
    public async Task TestConnection_StoredKeyToTheStoredCustomBaseUrl_IsAllowed_AndAuditsTheHost()
    {
        var profile = Stored();
        profile.Provider = "openai";
        profile.BaseUrl = "https://llm.example.com/v1";
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);
        _answer = """{"choices":[{"message":{"content":"OK"},"finish_reason":"stop"}]}""";

        var result = await _sut.TestConnectionAsync(
            Test() with { ProfileId = profile.Id, ApiKey = string.Empty, Provider = AiProvider.OpenAi, BaseUrl = "https://llm.example.com/v1/", Model = "gpt-5.5" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.StartsWith("https://llm.example.com/v1/", Assert.Single(_sent).Uri, StringComparison.Ordinal);
        await _audit.Received(1).LogAsync("ConnectionTestSucceeded", "AiRunnerProfile", profile.Id,
            "claude-sonnet (host llm.example.com)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestConnection_TypedKeyToAnotherBaseUrl_IsAllowed()
    {
        var profile = Stored();
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);

        var result = await _sut.TestConnectionAsync(
            Test() with { ProfileId = profile.Id, ApiKey = "sk-typed-key-777", BaseUrl = "https://proxy.example.com" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        var sent = Assert.Single(_sent);
        Assert.StartsWith("https://proxy.example.com/", sent.Uri, StringComparison.Ordinal);
        Assert.Equal("sk-typed-key-777", sent.Key);
        await _audit.Received(1).LogAsync("ConnectionTestSucceeded", "AiRunnerProfile", profile.Id,
            "claude-sonnet (host proxy.example.com)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestConnection_ProviderRefusal_IsAFailedResult_WithTheKeyMasked()
    {
        _status = HttpStatusCode.Unauthorized;
        _answer = "{\"error\":{\"message\":\"bad key sk-typed-key-777\"}}";

        var result = await _sut.TestConnectionAsync(Test() with { ApiKey = "sk-typed-key-777" }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("HTTP 401", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-typed-key-777", result.Message, StringComparison.Ordinal);
        await _audit.Received(1).LogAsync("ConnectionTestFailed", "AiRunnerProfile", 0, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestConnection_NoKeyAnywhere_IsRejectedWithoutCallingTheProvider()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.TestConnectionAsync(
            Test() with { ApiKey = string.Empty }, TestContext.Current.CancellationToken));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task R3_7_Get_ExposesTheListReadAtTheProvider_WithItsTimeAndError()
    {
        var profile = WithModelList(Stored());
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);

        var result = await _sut.GetProfileAsync(profile.Id, TestContext.Current.CancellationToken);

        Assert.Equal(["claude-haiku-5", "claude-opus-5-5"], result!.AvailableModels);
        Assert.Equal(new DateTime(2026, 10, 12, 1, 5, 0, DateTimeKind.Utc), result.AvailableModelsFetchedAt);
        Assert.Equal(new DateTime(2026, 10, 13, 1, 5, 0, DateTimeKind.Utc), result.ModelListCheckedAt);
        Assert.Equal("AI provider HTTP 503: provider error.", result.ModelListError);
    }

    [Fact]
    public void R3_7_Get_ADamagedOrMissingList_ReadsAsEmpty()
    {
        var damaged = Stored();
        damaged.AvailableModelsJson = "{not an array";

        Assert.Empty(AiRunnerProfileService.MapProfile(damaged).AvailableModels);
        Assert.Empty(AiRunnerProfileService.MapProfile(Stored()).AvailableModels);
    }

    [Fact]
    public async Task R3_7_Update_SameProviderUrlAndKey_KeepsTheList()
    {
        var profile = WithModelList(Stored());
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);

        await _sut.UpdateProfileAsync(profile.Id, Update() with { Model = "claude-opus-5-5" }, TestContext.Current.CancellationToken);

        Assert.NotNull(profile.AvailableModelsJson);
        Assert.NotNull(profile.ModelListCheckedAt);
    }

    [Theory]
    [InlineData(AiProvider.OpenAi, "", "")]
    [InlineData(AiProvider.Anthropic, "https://proxy.example.com", "")]
    [InlineData(AiProvider.Anthropic, "", "sk-rotated-999")]
    public async Task R3_7_Update_AnotherProviderUrlOrKey_ForgetsTheList(AiProvider provider, string baseUrl, string key)
    {
        var profile = WithModelList(Stored());
        _repo.FindProfileAsync(profile.Id, Arg.Any<CancellationToken>()).Returns(profile);

        await _sut.UpdateProfileAsync(profile.Id, Update() with { Provider = provider, BaseUrl = baseUrl, ApiKey = key },
            TestContext.Current.CancellationToken);

        Assert.Null(profile.AvailableModelsJson);
        Assert.Null(profile.AvailableModelsFetchedAt);
        Assert.Null(profile.ModelListCheckedAt);
        Assert.Null(profile.ModelListError);
    }

    [Fact]
    public async Task R3_7_ReadNow_ReadsTheSavedProfilesListWithItsStoredKey_SavesAndAudits()
    {
        var profile = WithModelList(Stored());
        profile.ModelListCheckedAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(-2);
        profile.ModelListError = "old failure";
        _repo.FindProfileAsync(3, Arg.Any<CancellationToken>()).Returns(profile);
        _answer = """{"data":[{"id":"claude-opus-5-5"},{"id":"claude-haiku-5"}]}""";

        var attempt = await _sut.RefreshModelListAsync(3, TestContext.Current.CancellationToken);

        Assert.NotNull(attempt);
        Assert.Null(attempt.RetryAfter);
        Assert.Equal(["claude-haiku-5", "claude-opus-5-5"], attempt.Profile.AvailableModels);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, attempt.Profile.AvailableModelsFetchedAt);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, attempt.Profile.ModelListCheckedAt);
        Assert.Null(attempt.Profile.ModelListError);
        var sent = Assert.Single(_sent);
        Assert.Equal(("https://api.anthropic.com/v1/models?limit=1000", StoredKey), (sent.Uri, sent.Key));
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("ModelListRefreshed", "AiRunnerProfile", 3, "claude-sonnet", Arg.Any<CancellationToken>());
        Assert.DoesNotContain(StoredKey, JsonSerializer.Serialize(attempt.Profile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R3_7_ReadNow_AFailure_KeepsTheList_StoresTheMaskedReason_AndNeverLogsTheKey()
    {
        var profile = WithModelList(Stored());
        _repo.FindProfileAsync(3, Arg.Any<CancellationToken>()).Returns(profile);
        _status = HttpStatusCode.Unauthorized;
        _answer = $$$"""{"error":{"message":"invalid x-api-key {{{StoredKey}}}"}}""";

        var attempt = await _sut.RefreshModelListAsync(3, TestContext.Current.CancellationToken);

        Assert.Equal(["claude-haiku-5", "claude-opus-5-5"], attempt!.Profile.AvailableModels);
        Assert.Contains("HTTP 401", attempt.Profile.ModelListError, StringComparison.Ordinal);
        Assert.DoesNotContain(StoredKey, attempt.Profile.ModelListError, StringComparison.Ordinal);
        Assert.All(_readerLogger.Records, entry => Assert.DoesNotContain(StoredKey, entry.Message, StringComparison.Ordinal));
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("ModelListRefreshFailed", "AiRunnerProfile", 3, "claude-sonnet", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    public async Task R3_7_ReadNow_WithinAMinuteOfTheLastRead_CallsNoProvider_AndSaysWhenToRetry(int secondsAgo)
    {
        var profile = WithModelList(Stored());
        profile.ModelListCheckedAt = _clock.GetUtcNow().UtcDateTime.AddSeconds(-secondsAgo);
        _repo.FindProfileAsync(3, Arg.Any<CancellationToken>()).Returns(profile);

        var attempt = await _sut.RefreshModelListAsync(3, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(60 - secondsAgo), attempt!.RetryAfter);
        Assert.Empty(_sent);
        await _repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(TestContext.Current.CancellationToken);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task R3_7_ReadNow_AMinuteAfterTheLastRead_CallsTheProviderAgain()
    {
        var profile = WithModelList(Stored());
        profile.ModelListCheckedAt = _clock.GetUtcNow().UtcDateTime.AddSeconds(-60);
        _repo.FindProfileAsync(3, Arg.Any<CancellationToken>()).Returns(profile);
        _answer = """{"data":[{"id":"claude-opus-5-5"}]}""";

        var attempt = await _sut.RefreshModelListAsync(3, TestContext.Current.CancellationToken);

        Assert.Null(attempt!.RetryAfter);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task R3_7_ReadNow_MissingProfile_IsNull_ACliProfileOrNoKey_IsRefused()
    {
        _repo.FindProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((AiRunnerProfile?)null);
        Assert.Null(await _sut.RefreshModelListAsync(99, TestContext.Current.CancellationToken));

        _repo.FindProfileAsync(4, Arg.Any<CancellationToken>()).Returns(CliProfile());
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.RefreshModelListAsync(4, TestContext.Current.CancellationToken));

        var keyless = Stored();
        keyless.ApiKeyEncrypted = string.Empty;
        _repo.FindProfileAsync(3, Arg.Any<CancellationToken>()).Returns(keyless);
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.RefreshModelListAsync(3, TestContext.Current.CancellationToken));
        Assert.Empty(_sent);
    }

    private static AiRunnerProfile WithModelList(AiRunnerProfile profile)
    {
        profile.AvailableModelsJson = """["claude-haiku-5","claude-opus-5-5"]""";
        profile.AvailableModelsFetchedAt = new DateTime(2026, 10, 12, 1, 5, 0, DateTimeKind.Utc);
        profile.ModelListCheckedAt = new DateTime(2026, 10, 13, 1, 5, 0, DateTimeKind.Utc);
        profile.ModelListError = "AI provider HTTP 503: provider error.";
        return profile;
    }

    private static CreateAiRunnerProfileRequest Valid() => new()
    {
        Name = "claude-sonnet",
        Provider = AiProvider.Anthropic,
        Model = "claude-sonnet-5-5",
        ApiKey = "sk-new-key-123456",
        ReasoningEffort = AiReasoningEffort.Low,
        MaxOutputTokens = 16_000,
        TimeoutSeconds = 600,
        MaxOutputBytes = 200_000,
        SendsDataExternally = true
    };

    private static UpdateAiRunnerProfileRequest Update() => new()
    {
        Name = "claude-sonnet",
        Provider = AiProvider.Anthropic,
        Model = "claude-sonnet-5-5",
        ReasoningEffort = AiReasoningEffort.Low
    };

    private static TestAiRunnerProfileRequest Test() => new()
    {
        Name = "claude-sonnet",
        Provider = AiProvider.Anthropic,
        Model = "claude-sonnet-5-5",
        ReasoningEffort = AiReasoningEffort.Low,
        TimeoutSeconds = 30
    };

    private static AiRunnerProfile Stored() => new()
    {
        Id = 3,
        OrganizationId = 7,
        Name = "claude-sonnet",
        Provider = "anthropic",
        Model = "claude-sonnet-5-5",
        ApiKeyEncrypted = "enc:" + StoredKey,
        ReasoningEffort = "low",
        MaxOutputTokens = 16_000,
        TimeoutSeconds = 600,
        MaxOutputBytes = 200_000
    };

    private static AiRunnerProfile CliProfile() => new()
    {
        Id = 4,
        OrganizationId = 7,
        Name = "claude-cli",
        Binary = "claude",
        ArgsTemplateJson = """["-p","{prompt_file}"]""",
        EnvironmentJsonEncrypted = "enc:{}"
    };

    private sealed class RecordingHandler(AiRunnerProfileServiceTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            test._sent.Add((request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null, body));
            return new HttpResponseMessage(test._status) { Content = new StringContent(test._answer, Encoding.UTF8, "application/json") };
        }
    }
}
