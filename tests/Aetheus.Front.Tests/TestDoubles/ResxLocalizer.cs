// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Xml.Linq;
using Aetheus.Front.Tests.Architecture;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// The application's own resource file, read as a localizer: a test that must assert on the sentence a
/// reader sees (its wording, the values placed in it) rather than on a key. A key missing from the file
/// comes back as the key, like the real localizer.
/// </summary>
internal sealed class ResxLocalizer : IStringLocalizer<Aetheus.Front.Resources.AppStrings>
{
    private readonly Dictionary<string, string> _values;
    private readonly CultureInfo _culture;

    public ResxLocalizer(string fileName, CultureInfo culture)
    {
        _culture = culture;
        _values = XDocument.Load(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Resources", fileName))
            .Root!.Elements("data")
            .ToDictionary(data => (string)data.Attribute("name")!, data => (string)data.Element("value")!);
    }

    public static ResxLocalizer French() => new("AppStrings.fr-FR.resx", CultureInfo.GetCultureInfo("fr-FR"));

    public static ResxLocalizer English() => new("AppStrings.resx", CultureInfo.InvariantCulture);

    public LocalizedString this[string name] =>
        _values.TryGetValue(name, out var value) ? new(name, value) : new(name, name, resourceNotFound: true);

    public LocalizedString this[string name, params object[] arguments] =>
        new(name, string.Format(_culture, this[name].Value, arguments));

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        _values.Select(pair => new LocalizedString(pair.Key, pair.Value));
}
