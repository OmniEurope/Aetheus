// SPDX-License-Identifier: EUPL-1.2
using System.Xml.Linq;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.Architecture;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R-19: the Apache log tab said "No Apache error log found" in raw English on a host whose log
/// existed but was unreadable to the agent. The agent's diagnostic line now becomes a sentence of the resource
/// files that names the path and, for a refused read, the installer option that grants it.</summary>
public sealed class ApacheLogDiagnosticTextTests
{
    [Theory]
    [InlineData("AppStrings.resx", "cannot be read")]
    [InlineData("AppStrings.fr-FR.resx", "n'est pas lisible")]
    public void AccessDenied_NamesThePath_AndWhatGrantsTheRead(string file, string refused)
    {
        var text = ApacheLogDiagnosticText.Describe(new ResxLocalizer(file), "[apache-log] AccessDenied /var/log/apache2/error.log");

        Assert.NotNull(text);
        Assert.Contains("/var/log/apache2/error.log", text, StringComparison.Ordinal);
        Assert.Contains(refused, text, StringComparison.Ordinal);
        Assert.Contains("--enable-apache-introspection", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[apache-log]", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AppStrings.resx", "No Apache log")]
    [InlineData("AppStrings.fr-FR.resx", "Aucun journal Apache")]
    public void NotFound_NamesEverySearchedPath(string file, string none)
    {
        var text = ApacheLogDiagnosticText.Describe(
            new ResxLocalizer(file), "[apache-log] NotFound /var/log/apache2/error.log /var/log/httpd/error_log");

        Assert.NotNull(text);
        Assert.Contains(none, text, StringComparison.Ordinal);
        Assert.Contains("/var/log/apache2/error.log, /var/log/httpd/error_log", text, StringComparison.Ordinal);
        Assert.DoesNotContain("introspection", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AppStrings.resx", "could not open")]
    [InlineData("AppStrings.fr-FR.resx", "n'a pas pu ouvrir")]
    public void ReadError_NamesThePath(string file, string failed)
    {
        var text = ApacheLogDiagnosticText.Describe(new ResxLocalizer(file), "[apache-log] ReadError /var/log/httpd/error_log");

        Assert.NotNull(text);
        Assert.Contains("/var/log/httpd/error_log", text, StringComparison.Ordinal);
        Assert.Contains(failed, text, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryLogLines_AreLeftToTheCodeBlock()
    {
        Assert.Null(ApacheLogDiagnosticText.Describe(new ResxLocalizer("AppStrings.fr-FR.resx"), "[core:error] AH00037: something"));
    }

    /// <summary>The values of one resource file, formatted like the real localizer does.</summary>
    private sealed class ResxLocalizer(string file) : IStringLocalizer
    {
        private readonly Dictionary<string, string> _values = XDocument
            .Load(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Resources", file))
            .Root!
            .Elements("data")
            .ToDictionary(element => (string)element.Attribute("name")!, element => (string?)element.Element("value") ?? string.Empty);

        public LocalizedString this[string name] => new(name, _values[name]);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(System.Globalization.CultureInfo.InvariantCulture, _values[name], arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
