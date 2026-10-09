// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests;

public class ListCacheServiceTests
{
    private static ListCacheService NewSut(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider();
        return new ListCacheService(time);
    }

    private static ListCacheService NewSut() => new(new FakeTimeProvider());

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        var sut = NewSut();

        var hit = sut.TryGet<List<int>>("releases:global", out var value);

        Assert.False(hit);
        Assert.Null(value);
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsStoredValue()
    {
        var sut = NewSut();
        var data = new List<string> { "1.0.0", "1.0.1" };

        sut.TryStore(sut.Begin(), "releases:project:1", data);
        var hit = sut.TryGet<List<string>>("releases:project:1", out var value);

        Assert.True(hit);
        Assert.Same(data, value);
    }

    [Fact]
    public void TryGet_TypeMismatch_ReturnsFalse()
    {
        var sut = NewSut();
        sut.TryStore(sut.Begin(), "k", new List<int> { 1 });

        var hit = sut.TryGet<List<string>>("k", out var value);

        Assert.False(hit);
        Assert.Null(value);
    }

    [Fact]
    public void Invalidate_RemovesOnlyThatKey()
    {
        var sut = NewSut();
        sut.TryStore(sut.Begin(), "a", new List<int> { 1 });
        sut.TryStore(sut.Begin(), "b", new List<int> { 2 });

        sut.Invalidate("a");

        Assert.False(sut.TryGet<List<int>>("a", out _));
        Assert.True(sut.TryGet<List<int>>("b", out _));
    }

    [Fact]
    public void InvalidatePrefix_RemovesEveryCachedPageForRealtimeEntityWithoutTouchingOthers()
    {
        var sut = NewSut();
        sut.TryStore(sut.Begin(), "servers:1:25", new List<int> { 1 });
        sut.TryStore(sut.Begin(), "servers:2:25", new List<int> { 2 });
        sut.TryStore(sut.Begin(), "users:1:50", new List<int> { 3 });

        sut.InvalidatePrefix("servers:");

        Assert.False(sut.TryGet<List<int>>("servers:1:25", out _));
        Assert.False(sut.TryGet<List<int>>("servers:2:25", out _));
        Assert.True(sut.TryGet<List<int>>("users:1:50", out _));
    }

    [Fact]
    public void Clear_EmptiesCache()
    {
        var sut = NewSut();
        sut.TryStore(sut.Begin(), "a", new List<int> { 1 });
        sut.TryStore(sut.Begin(), "b", new List<int> { 2 });

        sut.Clear();

        Assert.False(sut.TryGet<List<int>>("a", out _));
        Assert.False(sut.TryGet<List<int>>("b", out _));
    }

    [Fact]
    public void TryStore_NullAnswer_StoresNothing()
    {
        var sut = NewSut();

        Assert.True(sut.TryStore<List<int>>(sut.Begin(), "k", null));
        Assert.False(sut.TryGet<List<int>>("k", out _));
    }

    [Fact]
    public void IsFresh_WithinTtl_True_BeyondTtl_False()
    {
        var sut = NewSut(out var time);
        sut.TryStore(sut.Begin(), "project:1:activity", new List<int> { 1 });

        // Just under the 30s window - still fresh.
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True(sut.IsFresh("project:1:activity", TimeSpan.FromSeconds(30)));

        // Past the window - stale, so the consumer revalidates.
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(sut.IsFresh("project:1:activity", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void IsFresh_MissingKey_False()
    {
        var sut = NewSut();

        Assert.False(sut.IsFresh("absent", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Seed_Hit_AppliesCachedValue()
    {
        var sut = NewSut();
        var data = new List<int> { 1, 2 };
        sut.TryStore(sut.Begin(), "k", data);

        List<int>? applied = null;
        sut.Seed<List<int>>("k", v => applied = v);

        Assert.Same(data, applied);
    }

    [Fact]
    public void Seed_Miss_DoesNotInvokeApply()
    {
        var sut = NewSut();

        var called = false;
        sut.Seed<List<int>>("absent", _ => called = true);

        Assert.False(called);
    }

    [Fact]
    public async Task RevalidateAsync_ColdMiss_ShowsSpinnerThenFetchesAndCaches()
    {
        var sut = NewSut();
        var fresh = new List<int> { 9 };
        var loadingStates = new List<bool>();
        List<int>? applied = null;

        await sut.RevalidateAsync<List<int>>(
            "k",
            () => Task.FromResult(fresh),
            v => applied = v,
            loadingStates.Add,
            () => Task.CompletedTask);

        Assert.Same(fresh, applied);
        // Cold view: spinner on for the fetch, then off.
        Assert.Equal(new[] { true, false }, loadingStates);
        Assert.True(sut.TryGet<List<int>>("k", out var cached));
        Assert.Same(fresh, cached);
    }

    [Fact]
    public async Task RevalidateAsync_WarmHit_NeverShowsSpinner_AppliesCachedThenRevalidates()
    {
        var sut = NewSut();
        var stale = new List<int> { 1 };
        var fresh = new List<int> { 2 };
        sut.TryStore(sut.Begin(), "k", stale);

        var loadingStates = new List<bool>();
        var applied = new List<List<int>>();

        await sut.RevalidateAsync<List<int>>(
            "k",
            () => Task.FromResult(fresh),
            applied.Add,
            loadingStates.Add,
            () => Task.CompletedTask);

        // Warm view: the spinner is never turned on.
        Assert.Equal(new[] { false, false }, loadingStates);
        Assert.Same(stale, applied[0]); // cached applied first (instant paint)
        Assert.Same(fresh, applied[1]); // then revalidated in the background
        Assert.True(sut.TryGet<List<int>>("k", out var cached));
        Assert.Same(fresh, cached);
    }

    [Fact]
    public async Task RevalidateAsync_FetchThrowsHttp_KeepsCacheAndClearsLoading()
    {
        var sut = NewSut();
        var cachedData = new List<int> { 1 };
        sut.TryStore(sut.Begin(), "k", cachedData);
        var loadingStates = new List<bool>();

        await sut.RevalidateAsync<List<int>>(
            "k",
            () => throw new HttpRequestException("401"),
            _ => { },
            loadingStates.Add,
            () => Task.CompletedTask);

        Assert.False(loadingStates[^1]); // loading cleared even when the fetch fails
        Assert.True(sut.TryGet<List<int>>("k", out var still));
        Assert.Same(cachedData, still); // stale entry left intact on failure
    }

    [Fact]
    public async Task RevalidateAsync_NonAuthHttpFailure_NotifiesConsumerAndKeepsStaleValue()
    {
        var sut = NewSut();
        var stale = new List<int> { 1 };
        sut.TryStore(sut.Begin(), "k", stale);
        HttpRequestException? observed = null;

        await sut.RevalidateAsync<List<int>>(
            "k",
            () => throw new HttpRequestException(
                "backend unavailable",
                null,
                System.Net.HttpStatusCode.ServiceUnavailable),
            _ => { },
            _ => { },
            () => Task.CompletedTask,
            error => observed = error);

        Assert.NotNull(observed);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, observed.StatusCode);
        Assert.True(sut.TryGet<List<int>>("k", out var cached));
        Assert.Same(stale, cached);
    }

    [Fact]
    public async Task RevalidateAsync_Unauthorized_IsLeftToAuthProvider()
    {
        var sut = NewSut();
        var errorCallbackCalled = false;

        await sut.RevalidateAsync<List<int>>(
            "k",
            () => throw new HttpRequestException(
                "expired",
                null,
                System.Net.HttpStatusCode.Unauthorized),
            _ => { },
            _ => { },
            () => Task.CompletedTask,
            _ => errorCallbackCalled = true);

        Assert.False(errorCallbackCalled);
    }

    [Fact]
    public async Task RevalidateAsync_OlderCompletionCannotOverwriteNewerViewState()
    {
        var sut = NewSut();
        var firstCompletion = new TaskCompletionSource<List<int>>();
        var secondCompletion = new TaskCompletionSource<List<int>>();
        var applied = new List<List<int>>();
        Action<List<int>> apply = applied.Add;
        Action<bool> setLoading = _ => { };
        Func<Task> render = () => Task.CompletedTask;

        var first = sut.RevalidateAsync("first-filter", () => firstCompletion.Task, apply, setLoading, render);
        var second = sut.RevalidateAsync("second-filter", () => secondCompletion.Task, apply, setLoading, render);
        secondCompletion.SetResult([2]);
        await second;
        firstCompletion.SetResult([1]);
        await first;

        Assert.Single(applied);
        Assert.Equal([2], applied[0]);
    }

    // FRONT-020: a projects answer still in flight when the user signs out must not re-seed the cache
    // for whoever signs in next.
    [Fact]
    public async Task RevalidateAsync_AnswerArrivingAfterClear_IsNeitherCachedNorApplied()
    {
        var sut = NewSut();
        var pending = new TaskCompletionSource<List<string>>();
        var applied = new List<List<string>>();

        var load = sut.RevalidateAsync<List<string>>(
            "projects:1:25", () => pending.Task, applied.Add, _ => { }, () => Task.CompletedTask);
        sut.Clear();
        pending.SetResult(["account-a-project"]);
        await load;

        Assert.False(sut.TryGet<List<string>>("projects:1:25", out _));
        Assert.Empty(applied);
        var seeded = new List<List<string>>();
        sut.Seed<List<string>>("projects:1:25", seeded.Add);
        Assert.Empty(seeded);
    }

    [Fact]
    public async Task Entries_AreKeptPerAccount_AndAnAnswerIsStoredForTheAccountThatAskedForIt()
    {
        var auth = new AuthStateProvider(NSubstitute.Substitute.For<Microsoft.JSInterop.IJSRuntime>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthStateProvider>.Instance);
        var sut = new ListCacheService(new FakeTimeProvider(), auth);
        await auth.LoginAsync(Token("alice"));
        var pending = new TaskCompletionSource<List<string>>();
        var load = sut.RevalidateAsync<List<string>>(
            "projects:1:25", () => pending.Task, _ => { }, _ => { }, () => Task.CompletedTask);

        // Account B signs in (in the same browser runtime) before A's answer lands.
        await auth.LoginAsync(Token("bob"));
        pending.SetResult(["alice-project"]);
        await load;

        Assert.False(sut.TryGet<List<string>>("projects:1:25", out _));
        await auth.LoginAsync(Token("alice"));
        Assert.True(sut.TryGet<List<string>>("projects:1:25", out var alices));
        Assert.Equal(["alice-project"], alices);
    }

    private static string Token(string user)
    {
        static string Part(object value) => Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        return $"{Part(new { alg = "HS256" })}.{Part(new { sub = user, unique_name = user, exp })}.sig";
    }
}
