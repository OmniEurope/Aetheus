// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-26: the audience origin is the production address the generic template builds from the libraries the wizard wrote
/// (ADR-043). It is never guessed: no domain, an unresolved reference or a host that is not an address gives nothing.</summary>
public sealed class PipelineSetupProductionOriginTests
{
    private static Dictionary<string, string> Entries(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    [Fact]
    public void TheTemplateFormula_GivesTheDomain_WhenThereIsNoSubdomainNorPrefix()
    {
        var origin = PipelineSetupProductionOrigin.From(Entries(("DOMAIN", "atlas.example.com"), ("APP_SUBDOMAIN", "")));

        Assert.Equal("https://atlas.example.com", origin);
    }

    [Fact]
    public void TheSubdomain_IsAddedBeforeTheDomain_WithItsDot()
    {
        var origin = PipelineSetupProductionOrigin.From(Entries(("DOMAIN", "example.com"), ("APP_SUBDOMAIN", "atlas")));

        Assert.Equal("https://atlas.example.com", origin);
    }

    [Fact]
    public void TheHostPrefix_IsAddedInFront()
    {
        var origin = PipelineSetupProductionOrigin.From(Entries(("DOMAIN", "example.com"), ("HOST_PREFIX", "www.")));

        Assert.Equal("https://www.example.com", origin);
    }

    [Fact]
    public void AnExplicitHost_WinsOverTheFormula_AndItsReferencesAreResolved()
    {
        var origin = PipelineSetupProductionOrigin.From(Entries(("APP_HOST", "app.$(DOMAIN)"), ("DOMAIN", "example.com")));

        Assert.Equal("https://app.example.com", origin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankDomain_GivesNothing(string domain)
    {
        Assert.Null(PipelineSetupProductionOrigin.From(Entries(("DOMAIN", domain))));
    }

    [Fact]
    public void NoEntries_GiveNothing() => Assert.Null(PipelineSetupProductionOrigin.From(Entries()));

    [Fact]
    public void AReferenceThatStaysUnresolved_GivesNothing_NothingIsGuessed()
    {
        Assert.Null(PipelineSetupProductionOrigin.From(Entries(("APP_HOST", "$(MISSING).example.com"))));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("example.com/path")]
    [InlineData("example.com?x=1")]
    public void AHostThatIsNotAPublicAddress_GivesNothing(string host)
    {
        Assert.Null(PipelineSetupProductionOrigin.From(Entries(("APP_HOST", host))));
    }

    [Fact]
    public async Task ReadAsync_ReadsTheShareAndProductionLibrariesOfTheProject()
    {
        var libraries = Substitute.For<IVariableLibraryService>();
        libraries.ResolveLibrariesAsync(Arg.Any<List<string>>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Entries(("DOMAIN", "atlas.example.com")));

        var origin = await PipelineSetupProductionOrigin.ReadAsync(libraries, "atlas", 4, TestContext.Current.CancellationToken);

        Assert.Equal("https://atlas.example.com", origin);
        await libraries.Received(1).ResolveLibrariesAsync(
            Arg.Is<List<string>>(names => names.SequenceEqual(new[] { "atlas.share", "atlas.prod" })), 4, Arg.Any<CancellationToken>());
    }
}
