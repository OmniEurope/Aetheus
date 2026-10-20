// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>The XML flavour of a CycloneDX SBOM yields the same component facts as the JSON one, and a
/// document that is not a bill of materials is refused.</summary>
public sealed class CycloneDxXmlParserTests
{
    [Fact]
    public void ParseXml_ReadsEveryComponentFact()
    {
        const string xml = """
            <bom xmlns="http://cyclonedx.org/schema/bom/1.5">
              <components>
                <component type="library">
                  <name> Newtonsoft.Json </name>
                  <version>13.0.3</version>
                  <purl>pkg:nuget/Newtonsoft.Json@13.0.3</purl>
                  <scope>Required</scope>
                  <hashes><hash alg="SHA-256">abc123</hash></hashes>
                  <licenses>
                    <license><id>MIT</id></license>
                    <license><name>mit</name></license>
                    <expression>Apache-2.0 OR MIT</expression>
                  </licenses>
                </component>
                <component type="application">
                  <name>tool</name>
                </component>
              </components>
            </bom>
            """;

        var components = CycloneDxParser.Parse(xml, AnalysisReportFormat.CycloneDxXml);

        Assert.Equal(2, components.Count);
        var library = components[0];
        Assert.Equal("Newtonsoft.Json", library.Name);
        Assert.Equal("13.0.3", library.Version);
        Assert.Equal("pkg:nuget/Newtonsoft.Json@13.0.3", library.PackageUrl);
        Assert.Equal("library", library.ComponentType);
        Assert.Equal(["MIT", "Apache-2.0 OR MIT"], library.Licenses);
        Assert.Equal("abc123", library.Hash);
        Assert.True(library.IsDirect);
        var tool = components[1];
        Assert.Equal(string.Empty, tool.Version);
        Assert.Null(tool.PackageUrl);
        Assert.Empty(tool.Licenses);
        Assert.Null(tool.Hash);
        Assert.False(tool.IsDirect);
    }

    [Theory]
    [InlineData("<bom><components>", "Invalid CycloneDX XML report:")]
    [InlineData("<sbom />", "Invalid CycloneDX XML report: root element must be bom.")]
    [InlineData("<bom><components><component><name> </name></component></components></bom>", "CycloneDX component is missing name.")]
    public void ParseXml_RefusesWhatIsNotAValidBom(string xml, string message)
    {
        var error = Assert.Throws<BadRequestException>(() => CycloneDxParser.Parse(xml, AnalysisReportFormat.CycloneDxXml));

        Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OtherFormat_IsRefused()
    {
        var error = Assert.Throws<BadRequestException>(() => CycloneDxParser.Parse("{}", AnalysisReportFormat.Sarif));

        Assert.Equal("The report is not a CycloneDX document.", error.Message);
    }
}
