// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Layout;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// R1-66 with OE 1.8.1: the real preference chain (OmniPreferences, ApiPreferenceStore, the IUserPreferences
/// facade, OmniAppearanceState and the shell) follows the signed-in user without reloading the page.
/// Before sign-in nothing reaches the server and the look is the Aetheus default; after a sign-in, a
/// change of account in the same tab or a sign-out, no value of the previous account stays on screen.
/// The server is <see cref="PerUserPreferenceServer"/>, which answers for the account the browser is
/// signed in as.
/// </summary>
public sealed class PreferenceUserChangeTests : BunitContext
{
    private const int AliceId = 11;
    private const int BobId = 22;
    private static readonly TimeSpan Wait = BunitTestHelper.AsyncRenderWait;

    private readonly AuthStateProvider _auth;
    private readonly PerUserPreferenceServer _server;
    private readonly PreferenceStorageBrowser _browser;
    private string? _storedToken;

    public PreferenceUserChangeTests()
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: false);
        _browser = new PreferenceStorageBrowser(JSInterop.JSRuntime);
        Services.AddSingleton<IJSRuntime>(_browser);
        var js = Substitute.For<IJSRuntime>();
        js.InvokeAsync<string?>("localStorage.getItem", Arg.Is<object?[]>(a => a.Length == 1 && (string?)a[0] == StorageKeys.AuthToken))
            .Returns(_ => ValueTask.FromResult(_storedToken));
        _auth = new AuthStateProvider(js, NullLogger<AuthStateProvider>.Instance);
        Services.AddSingleton(_auth);
        _server = new PerUserPreferenceServer(() => _auth.UserId, handler);
        Services.AddSingleton(new ApiClient(new HttpClient(_server) { BaseAddress = new Uri("http://test/") }));
        // The production chain, in place of the helper's in-memory preferences.
        Services.AddOmniEuropePreferences<ApiPreferenceStore>(options => options.SyncInterval = Timeout.InfiniteTimeSpan);
        Services.AddScoped<IUserPreferences, UserPreferences>();
        Services.AddScoped(sp => new ActiveOrganizationService(sp.GetRequiredService<ApiClient>(), sp.GetRequiredService<IUserPreferences>()));
    }

    private OmniAppearanceState Appearance => Services.GetRequiredService<OmniAppearanceState>();

    private static string Token(int userId, string name)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var claims = new Dictionary<string, object>
        {
            ["unique_name"] = name,
            ["nameid"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        };
        return $"{Encode("{\"alg\":\"HS256\"}"u8.ToArray())}.{Encode(JsonSerializer.SerializeToUtf8Bytes(claims))}.sig";
    }

    private IRenderedComponent<MainLayout> RenderShell() => Render<MainLayout>(parameters => parameters.Add(
        layout => layout.Body, (RenderFragment)(builder =>
        {
            builder.OpenComponent<PreferenceProbe>(0);
            builder.CloseComponent();
        })));

    private void SeedAliceAndBob()
    {
        _server.Seed(AliceId, PreferenceKeys.ProjectViewMode, "alice-view");
        // Alice's former Aetheus appearance: light. Bob has never chosen one.
        _server.Seed(AliceId, PreferenceKeys.Appearance, new { theme = "light", density = "compact" });
        _server.Seed(BobId, PreferenceKeys.ProjectViewMode, "bob-view");
    }

    [Fact]
    public async Task BeforeSignIn_PreferencesUseTheDefaults_WritesStayInThePage_AndTheServerIsNeverCalled()
    {
        var preferences = Services.GetRequiredService<IUserPreferences>();
        var store = Services.GetRequiredService<IOmniPreferenceStore>();

        Assert.Equal(OmniPreferences.NoUserScope, await store.GetScopeAsync(Xunit.TestContext.Current.CancellationToken));
        Assert.Null(await preferences.GetAsync<string>(PreferenceKeys.ProjectViewMode, Xunit.TestContext.Current.CancellationToken));
        await preferences.SetAsync(PreferenceKeys.ProjectViewMode, "anonymous-view", Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("anonymous-view", await preferences.GetAsync<string>(PreferenceKeys.ProjectViewMode, Xunit.TestContext.Current.CancellationToken));
        Assert.Empty(_server.Requests);
        // Nor the browser cache: OmniPreferences keeps it in this page only.
        Assert.Empty(_browser.Storage);
    }

    [Fact]
    public async Task SignedOut_TheShellFollowsTheSystem_ThenSigningIn_ShowsTheUsersValues_WithoutTheSignedOutOnes()
    {
        SeedAliceAndBob();
        var cut = RenderShell();
        cut.WaitForAssertion(() => Assert.Equal(OmniAppearance.System, Appearance.Appearance), Wait);
        // R1-25: full width is the default, signed out as for a user who never chose.
        Assert.True(Appearance.FullWidth);
        var preferences = Services.GetRequiredService<IUserPreferences>();
        await cut.InvokeAsync(() => preferences.SetAsync(PreferenceKeys.ProjectViewMode, "anonymous-view"));
        Assert.Empty(_server.Requests);

        _storedToken = Token(AliceId, "alice");
        await cut.InvokeAsync(() => _auth.LoginAsync(_storedToken));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("alice-view", cut.Find(".probe").TextContent);
            Assert.Equal(OmniAppearance.Light, Appearance.Appearance);
            Assert.Equal(OmniDensity.Compact, Appearance.Density);
        }, Wait);
        // The value written while signed out is dropped, never saved for the user who signed in.
        Assert.Equal("\"alice-view\"", _server.Value(AliceId, PreferenceKeys.ProjectViewMode));
        Assert.DoesNotContain(_server.Requests, request => request.User is null && request.Method == "PUT");
    }

    [Fact]
    public async Task AnotherAccountInTheSameTab_ShowsOnlyItsOwnValues_WithoutReloadingThePage()
    {
        SeedAliceAndBob();
        _storedToken = Token(AliceId, "alice");
        await _auth.LoginAsync(_storedToken);
        var cut = RenderShell();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("alice-view", cut.Find(".probe").TextContent);
            Assert.Equal(OmniAppearance.Light, Appearance.Appearance);
            Assert.Equal("alice", cut.Find(".omni-app-menu__trigger-name").TextContent);
        }, Wait);

        // Another tab signs in as Bob: the browser storage event hands this tab Bob's token.
        _storedToken = Token(BobId, "bob");
        await cut.InvokeAsync(() => _auth.OnStorageChanged(StorageKeys.AuthToken, _storedToken));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("bob-view", cut.Find(".probe").TextContent);
            Assert.Equal("bob", cut.Find(".omni-app-menu__trigger-name").TextContent);
            // Bob never chose a look: the Aetheus default, not Alice's light compact one.
            Assert.Equal(OmniAppearance.System, Appearance.Appearance);
            Assert.Equal(OmniDensity.Comfortable, Appearance.Density);
            Assert.True(Appearance.FullWidth);
        }, Wait);
        Assert.DoesNotContain("alice", cut.Markup, StringComparison.Ordinal);
        // No full-page reload: the shell restarted the session in place.
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.DoesNotContain(navigation.History, entry => entry.Options.ForceLoad);
        // Nothing of Alice's was written into Bob's account, and Bob's look is kept as his own.
        Assert.Equal("\"bob-view\"", _server.Value(BobId, PreferenceKeys.ProjectViewMode));
        Assert.Contains("\"System\"", _server.Value(BobId, OmniAppearanceState.PreferenceKey), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Light\"", _server.Value(AliceId, OmniAppearanceState.PreferenceKey), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>R1-25: an appearance saved before the width could be chosen has no such member and is
    /// completed with full width (OE's own default is the centred column); a column the user chose
    /// (fullWidth false) is left alone. Run through the production preference chain.</summary>
    [Fact]
    public async Task AnAppearanceSavedWithoutAWidth_GetsFullWidth_AndAChosenCentredColumnStays()
    {
        _storedToken = Token(AliceId, "alice");
        await _auth.LoginAsync(_storedToken);
        var cut = RenderShell();
        cut.WaitForAssertion(() => Assert.True(Appearance.FullWidth), Wait);
        var omni = Services.GetRequiredService<OmniPreferences>();
        var migration = Services.GetRequiredService<Aetheus.Front.Components.Settings.LegacyAppearanceMigration>();

        // Saved by a version that had no width: the member is absent, the state reads it as centred.
        await cut.InvokeAsync(() => Appearance.SetFullWidthAsync(false));
        await cut.InvokeAsync(() => omni.SetAsync(OmniAppearanceState.PreferenceKey, new { version = 1, appearance = "Light" }));
        await cut.InvokeAsync(() => migration.RunAsync());

        Assert.True(Appearance.FullWidth);
        Assert.Contains("\"fullWidth\":true", _server.Value(AliceId, OmniAppearanceState.PreferenceKey), StringComparison.OrdinalIgnoreCase);

        // The user then chooses the centred column: the member is there, the migration keeps it.
        await cut.InvokeAsync(() => Appearance.SetFullWidthAsync(false));
        await cut.InvokeAsync(() => migration.RunAsync());

        Assert.False(Appearance.FullWidth);
        Assert.Contains("\"fullWidth\":false", _server.Value(AliceId, OmniAppearanceState.PreferenceKey), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SigningOut_LeavesNoValueOfTheAccountThatLeft()
    {
        SeedAliceAndBob();
        _storedToken = Token(AliceId, "alice");
        await _auth.LoginAsync(_storedToken);
        var cut = RenderShell();
        cut.WaitForAssertion(() => Assert.Equal(OmniAppearance.Light, Appearance.Appearance), Wait);
        var preferences = Services.GetRequiredService<IUserPreferences>();

        _storedToken = null;
        await cut.InvokeAsync(_auth.LogoutAsync);

        cut.WaitForAssertion(() => Assert.Equal(OmniAppearance.System, Appearance.Appearance), Wait);
        Assert.Equal(OmniDensity.Comfortable, Appearance.Density);
        Assert.Null(await cut.InvokeAsync(() => preferences.GetAsync<string>(PreferenceKeys.ProjectViewMode)));
    }

    /// <summary>A page that shows one preference, read once when it mounts, as the Aetheus pages do.</summary>
    private sealed class PreferenceProbe : ComponentBase
    {
        [Inject] private IUserPreferences Preferences { get; set; } = default!;

        private string? _value;

        protected override async Task OnInitializedAsync() =>
            _value = await Preferences.GetAsync<string>(PreferenceKeys.ProjectViewMode);

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "probe");
            builder.AddContent(2, _value ?? "none");
            builder.CloseElement();
        }
    }
}
