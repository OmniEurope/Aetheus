// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using Microsoft.EntityFrameworkCore;
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R1-8. A mail follows the recipient's own language and appearance as OmniEurope.Blazor stores them
/// (<c>omni.appearance</c>, not the <c>aetheus.appearance</c> key the front no longer writes), and a dark
/// recipient gets a dark mail: the template's colours and the fragments Aetheus writes into it.
/// </summary>
public sealed class MailRecipientAppearanceTests
{
    private static string Appearance(string mode, string? preset = null, string? palette = null) =>
        $$"""{"version":1,"appearance":"{{mode}}","preset":{{(preset is null ? "null" : $"\"{preset}\"")}},"palette":{{(palette is null ? "null" : $"\"{palette}\"")}},"font":null,"density":"Comfortable","backdropMotion":true,"textSize":5,"controlSize":5,"fullWidth":false}""";

    [Fact]
    public void TheAppearanceIsReadFromTheKeyOmniEuropeWrites_NotFromTheLegacyOne()
    {
        Assert.Equal("omni.appearance", MailRecipientStyle.AppearancePreferenceKey);
        Assert.Equal(OmniAppearanceState.PreferenceKey, MailRecipientStyle.AppearancePreferenceKey);
        Assert.NotEqual("aetheus.appearance", MailRecipientStyle.AppearancePreferenceKey);
    }

    [Fact]
    public async Task TheRecipientPreferences_AreReadUnderOmniAppearance_AndTheLegacyKeyIsIgnored()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        db.Users.AddRange(
            new User { Id = 1, Username = "dark", Email = "dark@example.org", IsActive = true },
            new User { Id = 2, Username = "legacy", Email = "legacy@example.org", IsActive = true });
        db.UserPreferences.AddRange(
            new UserPreference { UserId = 1, Key = "omni.appearance", Value = Appearance("Dark"), ChangedAt = DateTime.UtcNow },
            new UserPreference { UserId = 1, Key = "aetheus.appearance", Value = Appearance("Light"), ChangedAt = DateTime.UtcNow },
            new UserPreference { UserId = 1, Key = "aetheus.language", Value = "\"fr-FR\"", ChangedAt = DateTime.UtcNow },
            new UserPreference { UserId = 2, Key = "aetheus.appearance", Value = Appearance("Dark"), ChangedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new MailTemplateRepository(db);

        var dark = await repository.FindRecipientPreferencesAsync("dark@example.org", TestContext.Current.CancellationToken);
        var legacy = await repository.FindRecipientPreferencesAsync("legacy@example.org", TestContext.Current.CancellationToken);

        Assert.Equal(Appearance("Dark"), dark.AppearanceJson);
        Assert.Equal("\"fr-FR\"", dark.LanguageJson);
        Assert.Null(legacy.AppearanceJson);
        Assert.True(MailRecipientStyle.FromPreferences(null, dark.AppearanceJson).Theme.Dark);
        Assert.False(MailRecipientStyle.FromPreferences(null, legacy.AppearanceJson).Theme.Dark);
    }

    [Theory]
    [InlineData("Dark", true)]
    [InlineData("dark", true)]
    [InlineData("Light", false)]
    [InlineData("System", false)]
    [InlineData("Sepia", false)]
    public void OnlyAnExplicitDarkMode_PaintsTheMailDark(string mode, bool dark)
    {
        var style = MailRecipientStyle.FromPreferences(null, Appearance(mode));

        Assert.Equal(dark, style.Theme.Dark);
        Assert.Equal(dark, MailBlockColours.For(style.Theme) != MailBlockColours.Light);
    }

    [Fact]
    public void TheDarkTheme_IsTheDarkHalfOfTheRecipientsPresetAndPalette()
    {
        var preset = OmniThemePresets.All[^1];
        var palette = OmniThemePalettes.All[^1];

        var dark = MailRecipientStyle.FromPreferences(null, Appearance("Dark", preset.Name, palette.Name)).Theme;
        var light = MailRecipientStyle.FromPreferences(null, Appearance("Light", preset.Name, palette.Name)).Theme;

        Assert.Equal(OmniMailTheme.From(preset.With(palette), OmniAppearance.Dark), dark);
        Assert.Equal(OmniMailTheme.From(preset.With(palette)), light);
        Assert.NotEqual(light.Surface, dark.Surface);
        Assert.NotEqual(light.Text, dark.Text);
    }

    [Fact]
    public void ADarkRecipient_GetsADarkNotificationMail_FragmentsIncluded()
    {
        var dark = MailRecipientStyle.FromPreferences(null, Appearance("Dark")).Theme;
        var light = MailRecipientStyle.FromPreferences(null, Appearance("Light")).Theme;
        var email = NotificationEmailPresentationTests.FailedRun;

        var darkMail = NotificationMailKit.Build(email, "https://aetheus.example.org", appearanceJson: Appearance("Dark"));
        var lightMail = NotificationMailKit.Build(email, "https://aetheus.example.org", appearanceJson: Appearance("Light"));

        Assert.Contains(dark.PageBackground, darkMail.HtmlBody!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color-scheme", darkMail.HtmlBody!, StringComparison.OrdinalIgnoreCase);
        // The failed banner and badge: the dark theme's own pale-dark danger fill, not the fixed light one.
        Assert.Contains(dark.DangerSubtle, darkMail.HtmlBody!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#fde8e6", darkMail.HtmlBody!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MailBlockColours.Light.Ink, darkMail.HtmlBody!, StringComparison.OrdinalIgnoreCase);
        // The same event for a light recipient is unchanged: the fixed light pairs.
        Assert.Contains("#fde8e6", lightMail.HtmlBody!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(light.PageBackground, lightMail.HtmlBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryDarkThemeOfThePackage_KeepsAReadablePairForEveryTone()
    {
        foreach (var preset in OmniThemePresets.All)
            foreach (var palette in OmniThemePalettes.All.Cast<OmniThemePalette?>().Prepend(null))
            {
                var theme = OmniMailTheme.From(palette is { } chosen ? preset.With(chosen) : preset, OmniAppearance.Dark);
                var colours = MailBlockColours.For(theme);
                foreach (var tone in Enum.GetValues<MailTone>())
                {
                    var (background, ink) = colours.Pair(tone);
                    // Where the theme's colours are plain hexadecimal the pair reads at 4.5:1 or more (WCAG AA);
                    // any other notation is not measured here and falls back to the theme's text colour.
                    var contrast = MailBlockColours.Contrast(background, ink);
                    Assert.True(contrast == 0 ? ink == theme.Text : contrast >= 4.5,
                        $"{preset.Name}/{palette?.Name}/{tone}: {ink} on {background} = {contrast:0.00}");
                }
            }
    }

    [Fact]
    public void TheLightPairs_AreTheOnesTheMailsAlwaysHad()
    {
        Assert.Equal(("#fde8e6", "#a3231a"), MailBlockColours.Light.Pair(MailTone.Danger));
        Assert.Equal(("#e3f4e8", "#17643a"), MailBlockColours.Light.Pair(MailTone.Success));
        Assert.Equal(("#fff3d6", "#7a4b00"), MailBlockColours.Light.Pair(MailTone.Warning));
        Assert.Equal(("#eef0f3", "#404b5a"), MailBlockColours.Light.Pair(MailTone.Neutral));
        Assert.Same(MailBlockColours.Light, MailBlockColours.For(null));
        Assert.Same(MailBlockColours.Light, MailBlockColours.For(OmniMailTheme.Default));
    }

    [Fact]
    public void ADefaultLanguage_OnlyFillsInWhenTheRecipientChoseNone()
    {
        Assert.Equal("fr", MailRecipientStyle.FromPreferences(null, null, "fr-FR").Language);
        Assert.Equal("en", MailRecipientStyle.FromPreferences("\"en\"", null, "fr-FR").Language);
        Assert.Equal("fr", MailRecipientStyle.FromPreferences("\"fr\"", null, "en").Language);
        Assert.Equal("en", MailRecipientStyle.FromPreferences(null, null).Language);
    }
}
