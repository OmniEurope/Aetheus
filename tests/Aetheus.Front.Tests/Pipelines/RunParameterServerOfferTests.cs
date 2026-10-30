// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary>
/// Recette R-76: the production server a deployment runs on is offered as a choice among the project's servers,
/// the production environment's one preselected, instead of a text field the user types a name in.
/// </summary>
public sealed class RunParameterServerOfferTests
{
    private static PipelineRunParameterDto ServerParameter(
        string type = "string", string? defaultValue = null, bool required = true, List<string>? allowed = null, string name = "deployServer") => new()
        {
            Name = name,
            Type = type,
            Default = defaultValue,
            Required = required,
            AllowedValues = allowed ?? []
        };

    [Theory]
    [InlineData("string")]
    [InlineData("String")]
    [InlineData("")]
    public void ARequiredTextServerParameterWithoutDefault_IsAwaitingAServer(string type) =>
        Assert.True(RunParameterServerOffer.IsAwaitingServer(ServerParameter(type)));

    [Fact]
    public void AParameterWithAnotherName_IsNotAwaitingAServer() =>
        Assert.False(RunParameterServerOffer.IsAwaitingServer(ServerParameter(name: "branch")));

    [Fact]
    public void AnOptionalParameter_IsNotAwaitingAServer() =>
        Assert.False(RunParameterServerOffer.IsAwaitingServer(ServerParameter(required: false)));

    [Theory]
    [InlineData("web-1")]
    [InlineData("  x ")]
    public void AParameterWithADefault_IsNotAwaitingAServer(string defaultValue) =>
        Assert.False(RunParameterServerOffer.IsAwaitingServer(ServerParameter(defaultValue: defaultValue)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankDefaultCountsAsNoDefault(string defaultValue) =>
        Assert.True(RunParameterServerOffer.IsAwaitingServer(ServerParameter(defaultValue: defaultValue)));

    [Fact]
    public void AParameterThatAlreadyHasAllowedValues_IsNotAwaitingAServer() =>
        Assert.False(RunParameterServerOffer.IsAwaitingServer(ServerParameter(allowed: ["a", "b"])));

    [Theory]
    [InlineData("choice")]
    [InlineData("bool")]
    public void AParameterThatIsNotText_IsNotAwaitingAServer(string type) =>
        Assert.False(RunParameterServerOffer.IsAwaitingServer(ServerParameter(type)));

    [Theory]
    [InlineData("prod", true)]
    [InlineData("production", true)]
    [InlineData("PROD", true)]
    [InlineData("Production", true)]
    [InlineData("staging", false)]
    [InlineData("preprod", false)]
    [InlineData("", false)]
    public void IsProductionEnvironment_ReadsTheNamesTheProductionSetupUses(string name, bool expected) =>
        Assert.Equal(expected, RunParameterServerOffer.IsProductionEnvironment(name));

    [Fact]
    public void WithOneProductionServer_TheParameterBecomesAChoicePreselectedOnIt()
    {
        var result = RunParameterServerOffer.Apply([ServerParameter()], ["web-2", "web-1"], ["web-2"]);

        var parameter = Assert.Single(result);
        Assert.Equal("choice", parameter.Type);
        Assert.Equal(["web-1", "web-2"], parameter.AllowedValues);
        Assert.Equal("web-2", parameter.Default);
    }

    [Fact]
    public void WithSeveralProductionServers_NothingIsPreselected()
    {
        var result = RunParameterServerOffer.Apply([ServerParameter()], ["web-1"], ["web-2", "web-3"]);

        var parameter = Assert.Single(result);
        Assert.Equal(["web-1", "web-2", "web-3"], parameter.AllowedValues);
        Assert.Null(parameter.Default);
    }

    [Fact]
    public void WithoutProductionServerAndOneProjectServer_ThatServerIsPreselected()
    {
        var result = RunParameterServerOffer.Apply([ServerParameter()], ["web-1"], []);

        var parameter = Assert.Single(result);
        Assert.Equal("choice", parameter.Type);
        Assert.Equal(["web-1"], parameter.AllowedValues);
        Assert.Equal("web-1", parameter.Default);
    }

    [Fact]
    public void WithoutProductionServerAndSeveralProjectServers_NothingIsGuessed()
    {
        var result = RunParameterServerOffer.Apply([ServerParameter()], ["web-1", "web-2"], []);

        var parameter = Assert.Single(result);
        Assert.Equal(["web-1", "web-2"], parameter.AllowedValues);
        Assert.Null(parameter.Default);
    }

    [Fact]
    public void BlankAndDuplicateNamesAreDropped_AndTheChoicesAreSorted()
    {
        var result = RunParameterServerOffer.Apply([ServerParameter()], ["b", null, "  ", "a", "b"], ["a", null]);

        var parameter = Assert.Single(result);
        Assert.Equal(["a", "b"], parameter.AllowedValues);
        Assert.Equal("a", parameter.Default);
    }

    [Fact]
    public void WithNoServerToOffer_TheSameListIsReturned()
    {
        List<PipelineRunParameterDto> declared = [ServerParameter()];

        var result = RunParameterServerOffer.Apply(declared, [null, " "], [null]);

        Assert.Same(declared, result);
        Assert.Equal("string", result[0].Type);
        Assert.Empty(result[0].AllowedValues);
    }

    [Fact]
    public void OtherParameters_AndAServerParameterAlreadyChosen_AreLeftAlone()
    {
        var other = ServerParameter(name: "branch");
        var alreadyChoice = ServerParameter(type: "choice", allowed: ["x"]);
        var withDefault = ServerParameter(defaultValue: "web-9");

        var result = RunParameterServerOffer.Apply([other, alreadyChoice, withDefault], ["web-1"], ["web-1"]);

        Assert.Equal(3, result.Count);
        Assert.Equal(other, result[0]);
        Assert.Equal(alreadyChoice, result[1]);
        Assert.Equal(withDefault, result[2]);
    }
}
