// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests.PackageRegistry;

public sealed class MavenPomInspectorTests
{
    private static MavenRepositoryPath Path(string raw = "org/example/demo/1.0/demo-1.0.pom")
        => MavenRepositoryPath.TryParse(raw, out var parsed, out var error) ? parsed! : throw new InvalidOperationException(error);

    private static MavenPom Inspect(string xml, MavenRepositoryPath? path = null)
        => MavenPomInspector.Inspect(Encoding.UTF8.GetBytes(xml), path ?? Path());

    [Fact]
    public void AMatchingPom_ReturnsItsDescription()
    {
        var pom = Inspect("<project><groupId>org.example</groupId><artifactId>demo</artifactId><version>1.0</version><description> Lib </description></project>");

        Assert.Equal("Lib", pom.Description);
    }

    [Fact]
    public void GroupAndVersion_FallBackToTheParent_AndNameToTheDescription()
    {
        var pom = Inspect("""
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <parent><groupId>org.example</groupId><artifactId>root</artifactId><version>1.0</version></parent>
              <artifactId>demo</artifactId><name>Demo name</name>
            </project>
            """);

        Assert.Equal("Demo name", pom.Description);
    }

    [Fact]
    public void AnUnresolvedPropertyIsNotCompared()
    {
        var pom = Inspect("<project><groupId>org.example</groupId><artifactId>demo</artifactId><version>${revision}</version></project>");

        Assert.Null(pom.Description);
    }

    [Fact]
    public void ALongDescription_IsTruncated()
    {
        var pom = Inspect($"<project><groupId>org.example</groupId><artifactId>demo</artifactId><version>1.0</version><description>{new string('a', 2000)}</description></project>");

        Assert.Equal(1000, pom.Description!.Length);
    }

    [Theory]
    [InlineData("<project><groupId>org.example</groupId><artifactId>demo</artifactId>")]
    [InlineData("not xml at all")]
    public void MalformedXml_IsABadRequest(string xml)
        => Assert.Throws<BadRequestException>(() => Inspect(xml));

    [Fact]
    public void AnotherRootElement_IsRejected()
        => Assert.Throws<BadRequestException>(() => Inspect("<settings/>"));

    [Theory]
    [InlineData("<project><groupId>org.other</groupId><artifactId>demo</artifactId><version>1.0</version></project>")]
    [InlineData("<project><groupId>org.example</groupId><artifactId>other</artifactId><version>1.0</version></project>")]
    [InlineData("<project><groupId>org.example</groupId><artifactId>demo</artifactId><version>2.0</version></project>")]
    [InlineData("<project><groupId>org.example</groupId><artifactId>demo</artifactId></project>")]
    [InlineData("<project><artifactId>demo</artifactId><version>1.0</version></project>")]
    public void ACoordinateThatDiffersOrIsMissing_IsRejected(string xml)
        => Assert.Throws<BadRequestException>(() => Inspect(xml));

    [Fact]
    public void ADoctypeDeclaringAnExternalEntity_IsRejected()
        => Assert.ThrowsAny<Exception>(() => Inspect(
            "<!DOCTYPE project [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><project><groupId>&x;</groupId><artifactId>demo</artifactId><version>1.0</version></project>"));
}
