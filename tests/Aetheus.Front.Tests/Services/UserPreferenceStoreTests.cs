// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Tests.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace Aetheus.Front.Tests.Services;

/// <summary>
/// R1-66: the browser side of the per-user preferences. Before sign-in the scope is OE's NoUserScope; the
/// store answers only for the signed-in user, never with the cache opened for another account until the
/// scope is asked again (OmniPreferences.ResetUserAsync); the scope is an opaque hash; the values over the
/// server's bounds are refused before any request. The change of user itself is in PreferenceUserChangeTests.
/// </summary>
public sealed class UserPreferenceStoreTests
{
    private readonly BunitTestHelper.TestHandler _handler = new();
    private readonly AuthStateProvider _auth = new(Substitute.For<IJSRuntime>(), NullLogger<AuthStateProvider>.Instance);

    private ApiClient Api => new(new HttpClient(_handler) { BaseAddress = new Uri("http://test/") });

    private static string Token(int? userId, string username)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var claims = new Dictionary<string, object> { ["unique_name"] = username, ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() };
        if (userId is { } id)
            claims["nameid"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{Encode("{\"alg\":\"HS256\"}"u8.ToArray())}.{Encode(JsonSerializer.SerializeToUtf8Bytes(claims))}.sig";
    }

    [Fact]
    public async Task Store_WithoutASignedInUser_GivesNoUserScope_RefusesEveryOtherCall_AndSendsNothing()
    {
        var store = new ApiPreferenceStore(Api, new PreferenceScope(_auth));
        var ct = TestContext.Current.CancellationToken;

        // OE 1.8.1: an empty scope is no longer refused; it means that nobody is signed in yet.
        Assert.Equal(OmniPreferences.NoUserScope, await store.GetScopeAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("aetheus.language", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new OmniPreference("aetheus.language", "\"en\"", DateTimeOffset.UtcNow), ct));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Store_ForAnIdentityWithoutUserRow_GivesNoUserScope_AndRefusesReads()
    {
        await _auth.LoginAsync(Token(null, "admin"));
        var store = new ApiPreferenceStore(Api, new PreferenceScope(_auth));

        Assert.Equal(OmniPreferences.NoUserScope, await store.GetScopeAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("aetheus.language", TestContext.Current.CancellationToken));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Scope_IsAnOpaqueStableHashOfTheUserId_WithoutColon()
    {
        await _auth.LoginAsync(Token(42, "alice"));
        var store = new ApiPreferenceStore(Api, new PreferenceScope(_auth));

        var scope = await store.GetScopeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PreferenceScope.For(42), scope);
        Assert.NotEqual(PreferenceScope.For(43), scope);
        Assert.DoesNotContain(":", scope, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", scope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherAccountOnTheSamePage_IsRefused_UntilTheScopeIsAskedAgain()
    {
        await _auth.LoginAsync(Token(42, "alice"));
        var scope = new PreferenceScope(_auth);
        var store = new ApiPreferenceStore(Api, scope);
        var ct = TestContext.Current.CancellationToken;
        await store.GetScopeAsync(ct);

        await _auth.LogoutAsync();
        await _auth.LoginAsync(Token(7, "bob"));

        Assert.True(scope.OpenedForAnotherUser);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetChangedSinceAsync(null, ct));
        Assert.Empty(_handler.Requests);

        // OmniPreferences.ResetUserAsync asks for the scope again: the store then answers for the new account.
        _handler.SetJsonResponse("api/users/me/preferences", new List<UserPreferenceDto>());
        Assert.Equal(PreferenceScope.For(7), await store.GetScopeAsync(ct));
        Assert.False(scope.OpenedForAnotherUser);
        Assert.Empty(await store.GetChangedSinceAsync(null, ct));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Store_MapsServerAnswers_AndSendsTheDateOfTheChange()
    {
        await _auth.LoginAsync(Token(42, "alice"));
        var store = new ApiPreferenceStore(Api, new PreferenceScope(_auth));
        var ct = TestContext.Current.CancellationToken;
        var changedAt = new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
        _handler.SetResponse(HttpMethod.Get, "api/users/me/preferences/aetheus.language", HttpStatusCode.NoContent);
        _handler.SetResponse(HttpMethod.Put, "api/users/me/preferences/aetheus.language", HttpStatusCode.OK);
        _handler.SetJsonResponse("api/users/me/preferences?since=", new List<UserPreferenceDto>
        {
            new() { Key = "omni.appearance", Value = null, UpdatedAt = changedAt }
        });

        Assert.Null(await store.GetAsync("aetheus.language", ct));
        var changed = Assert.Single(await store.GetChangedSinceAsync(changedAt.AddDays(-1), ct));
        Assert.Equal(new OmniPreference("omni.appearance", null, changedAt), changed);

        await store.SaveAsync(new OmniPreference("aetheus.language", "\"fr-FR\"", changedAt), ct);
        var put = Assert.Single(_handler.RequestDetails, request => request.Method == "PUT");
        var sent = JsonSerializer.Deserialize<SaveUserPreferenceRequest>(put.Body!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("\"fr-FR\"", sent.Value);
        Assert.Equal(changedAt, sent.UpdatedAt);
    }

    [Fact]
    public async Task Store_RefusesAValueOverTheServerBound_BeforeSendingIt()
    {
        await _auth.LoginAsync(Token(42, "alice"));
        var store = new ApiPreferenceStore(Api, new PreferenceScope(_auth));
        var tooLong = "\"" + new string('x', UserPreferenceRules.MaxValueLength) + "\"";

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync(new OmniPreference("aetheus.appearance", tooLong, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync(new OmniPreference("bad key", "1", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void Migration_Plan_MapsEveryFormerBrowserKey_AndSkipsWhatNoLongerReads()
    {
        var local = new Dictionary<string, string?>
        {
            ["aetheus.projects.favorites"] = "[3,7]",
            ["aetheus.projects.view-mode"] = "Compact",
            ["aetheus.activeOrgId"] = "5",
            ["servers-columns-hidden"] = "Tags,Hostname",
            ["aetheus.pipelines.view-tab.alice"] = "runs",
            ["aetheus.pipelines.view-tab.bob"] = "catalog",
            ["aetheus.unmet-dismissed.12"] = "Vault:prod",
            ["vp-layout-9"] = "{\"sig\":\"1:build\",\"pos\":{\"0\":[1,2]}}",
            ["vp-layout-10"] = "{not json",
            ["run-tl-exp-4"] = "[99]",
            ["run-tl-exp-5"] = "[]"
        };

        var plan = LegacyPreferenceMigration.Plan(local, "alice").ToDictionary(entry => entry.Key, entry => entry.Value.GetRawText());

        Assert.Equal("[3,7]", plan[PreferenceKeys.ProjectFavorites]);
        Assert.Equal("\"Compact\"", plan[PreferenceKeys.ProjectViewMode]);
        Assert.Equal("5", plan[PreferenceKeys.ActiveOrganization]);
        Assert.Equal("\"Tags,Hostname\"", plan[PreferenceKeys.ServerHiddenColumns]);
        Assert.Equal("\"runs\"", plan[PreferenceKeys.PipelineListView]);
        Assert.Equal("\"Vault:prod\"", plan[PreferenceKeys.UnmetDismissed(12)]);
        Assert.Contains("1:build", plan[PreferenceKeys.VisualLayout(9)], StringComparison.Ordinal);
        Assert.False(plan.ContainsKey(PreferenceKeys.VisualLayout(10)));
        Assert.Equal("{\"4\":[99],\"5\":[]}", plan[PreferenceKeys.RunTimelineExpanded]);
        Assert.Equal(8, plan.Count);
    }

    [Fact]
    public async Task RunTimelineExpansion_KeepsOnlyTheRunsChangedLast()
    {
        var preferences = new InMemoryUserPreferences();
        var store = new RunTimelineExpansionStore(preferences);

        for (var runId = 1; runId <= RunTimelineExpansionStore.MaxRuns + 5; runId++)
            await store.SaveAsync(runId, [runId * 10]);

        var map = preferences.Read<Dictionary<string, int[]>>(PreferenceKeys.RunTimelineExpanded)!;
        Assert.Equal(RunTimelineExpansionStore.MaxRuns, map.Count);
        Assert.Null(await store.LoadAsync(1));
        var last = await store.LoadAsync(RunTimelineExpansionStore.MaxRuns + 5);
        Assert.Equal(new[] { 550 }, last);
    }
}
