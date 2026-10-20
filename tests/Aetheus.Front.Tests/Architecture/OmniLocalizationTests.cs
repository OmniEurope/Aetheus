// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OmniStrings = OmniEurope.Blazor.Resources.AppStrings;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>OE component text resolves in supported cultures and falls back to neutral library
/// resources for unknown cultures.</summary>
public sealed class OmniLocalizationTests
{
    public static TheoryData<string> Cultures => new() { "fr-FR", "en-US", "de-DE" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void EveryLibraryText_ResolvesInTheCulture(string culture)
    {
        var keys = new ResourceManager("OmniEurope.Blazor.Resources.AppStrings", typeof(OmniStrings).Assembly)
            .GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToList();
        Assert.True(keys.Count > 50, $"Only {keys.Count} library keys were read: the scan is broken.");

        var services = new ServiceCollection().AddLogging().AddOmniEuropeBlazor().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<OmniStrings>>();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            var missing = keys.Where(key => localizer[key] is { ResourceNotFound: true } or { Value.Length: 0 }).ToList();

            Assert.True(missing.Count == 0, $"{culture}: {missing.Count} library texts do not resolve: {string.Join(", ", missing.Take(20))}");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
