// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Aetheus.Front.Components.Settings;

/// <summary>
/// R1-66 with OE 1.8.1: follows the signed-in user without reloading the page. Run at page start and
/// after every change of the authentication state (sign-in, sign-out, another account): when the user
/// differs from the one the preferences were last taken for, <see cref="OmniPreferences.ResetUserAsync"/>
/// reads every preference of this page again for the new user (OmniAppearanceState, the grids with a
/// state key and every <see cref="IUserPreferences.Changed"/> listener follow). Then, once per user and
/// page, it copies this browser's former preferences, writes the former appearance when the user has none
/// in OE yet, and takes the user's language: a language that differs from the one the page runs in still
/// reloads the page in it (the satellite resources are loaded at start only), the way picking a language
/// does. Nobody signed in: the appearance is the Aetheus default (dark), held in memory only.
/// </summary>
public sealed class UserPreferenceSession(
    OmniPreferences omni,
    IUserPreferences preferences,
    LegacyPreferenceMigration migration,
    LegacyAppearanceMigration appearance,
    PreferenceScope scope,
    AuthStateProvider auth,
    IJSRuntime js,
    NavigationManager navigation,
    ILogger<UserPreferenceSession> logger)
{
    private static readonly string[] Languages = ["en", "fr-FR"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _taken;
    private string? _user;
    private string? _settledFor;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var user = scope.Current;
            if (!_taken || !string.Equals(user, _user, StringComparison.Ordinal))
            {
                _taken = true;
                _user = user;
                _settledFor = null;
                await omni.ResetUserAsync(ct);
                if (user is null)
                    await appearance.ApplyDefaultAsync();
            }

            // A forced password change holds every other request until it is done (SITE-SEC-001).
            if (user is null || auth.MustChangePassword || _settledFor == user)
                return;
            _settledFor = user;
            await SettleAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SettleAsync(CancellationToken ct)
    {
        try
        {
            var copied = await migration.RunAsync(auth.Username, ct);
            if (copied is { } count)
                logger.LogInformation("Copied {Count} preference(s) of this browser to the signed-in user's account", count);
            await appearance.RunAsync(ct);
            await SyncLanguageAsync(ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or JSException or TaskCanceledException)
        {
            // Mandatory: the session must start even when the preferences cannot be reached; the cache
            // of this browser keeps answering and OmniPreferences synchronizes later.
            logger.LogWarning(exception, "The user preferences could not be synchronized at session start");
        }
    }

    private async Task SyncLanguageAsync(CancellationToken ct)
    {
        var language = await preferences.GetAsync<string>(PreferenceKeys.Language, ct);
        if (language is null || !Languages.Contains(language, StringComparer.Ordinal))
            return;
        if (await js.InvokeAsync<string?>("localStorage.getItem", ct, StorageKeys.Lang) != language)
            await js.InvokeVoidAsync("localStorage.setItem", ct, StorageKeys.Lang, language);
        if (string.Equals(language, Aetheus.Front.Layout.MainLayout.CurrentLanguage, StringComparison.Ordinal))
            return;
        await js.InvokeVoidAsync("Aetheus.setLang", ct, language);
        navigation.NavigateTo(navigation.Uri, forceLoad: true);
    }
}
