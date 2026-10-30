// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Settings;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Settings;

/// <summary>
/// R-42: a new user's first login gets the Aetheus default, never the look of the previous account of the
/// same browser. The former browser keys of the appearance carry no user, so they cannot be attributed to
/// the account that signs in: only the former preference stored on the server for that very user is a
/// source. The stale browser keys are cleared so no later account can receive them either.
/// </summary>
public sealed class LegacyAppearanceMigrationBrowserKeysTests : BunitContext
{
    private readonly InMemoryUserPreferences _preferences;

    public LegacyAppearanceMigrationBrowserKeysTests()
    {
        BunitTestHelper.RegisterServices(this);
        _preferences = Services.GetRequiredService<InMemoryUserPreferences>();
        // The browser was used before by another account: light, compact, motion off.
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.Theme).SetResult("light");
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.Density).SetResult("compact");
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.BackdropMotion).SetResult("off");
    }

    private OmniAppearanceState State => Services.GetRequiredService<OmniAppearanceState>();

    private LegacyAppearanceMigration Migration => Services.GetRequiredService<LegacyAppearanceMigration>();

    [Fact]
    public async Task ANewAccount_DoesNotInheritTheAppearanceOfThePreviousAccountOfTheBrowser()
    {
        await Migration.RunAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(OmniAppearance.System, State.Appearance);
        Assert.Equal(OmniDensity.Comfortable, State.Density);
        Assert.True(State.BackdropMotion);
        Assert.True(State.FullWidth);
    }

    [Fact]
    public async Task TheStaleBrowserKeys_AreClearedSoNoLaterAccountReceivesThem()
    {
        await Migration.RunAsync(Xunit.TestContext.Current.CancellationToken);

        // One removal per former key (theme, preset, palette, font, density, text size, control size, motion).
        JSInterop.VerifyInvoke("localStorage.removeItem", 8);
    }

    [Fact]
    public async Task AUserWithAFormerServerPreference_StillGetsIt_NotTheBrowserKeys()
    {
        _preferences.Seed(PreferenceKeys.Appearance, new { theme = "system", density = "spacious" });

        await Migration.RunAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(OmniAppearance.System, State.Appearance);
        Assert.Equal(OmniDensity.Spacious, State.Density);
        Assert.True(State.BackdropMotion);
    }
}
