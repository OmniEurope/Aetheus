// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using YamlDotNet.Serialization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-36 (run 3119): what the builder writes for the pair the wizard chains, the candidate that starts the production
/// deployment on success. The follower is given no value at launch (the edge carries only the release), so the server the user
/// chose has to be the default of its <c>deployServer</c> parameter, whatever the shape of the production preparation.</summary>
public sealed class PipelineSetupYamlBuilderChainServerTests
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private static PipelineSetupYamlInput Input() => new(
        "atlas", (true, true, false), "package-web-analytics-dotnet",
        Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: false, SecurityHistory: false,
        ["candidate", "deploy-prod"], RunDeployAfterCandidate: true);

    private static Dictionary<object, object> Parameter(string yaml) =>
        (Dictionary<object, object>)((List<object>)Yaml.Deserialize<Dictionary<object, object>>(yaml)["parameters"]).Single();

    public static TheoryData<string?, string?[]> Preparations => new()
    {
        // The server of the production environment, among several.
        { "vps-b", ["vps-a", "vps-b"] },
        // A chosen server of a project that lists none.
        { "vps-b", [] },
        // The project's only server.
        { "vps-a", ["vps-a"] }
    };

    [Theory]
    [MemberData(nameof(Preparations))]
    public void TheChainedDeployProd_CarriesTheChosenServerAsTheDefaultOfDeployServer(string? chosen, string?[] servers)
    {
        var production = new PipelineSetupProductionInput("deploy/compose.yml", "replace", chosen, [.. servers.OfType<string>()]);

        var candidate = PipelineSetupYamlBuilder.Build("atlas-candidate", "application-candidate", 3, Input(), production);
        var deploy = PipelineSetupYamlBuilder.Build("atlas-deploy-prod", "application-deploy-prod", 3, Input(), production);

        Assert.Contains("pipeline: atlas-deploy-prod", candidate, StringComparison.Ordinal);
        var parameter = Parameter(deploy);
        Assert.Equal("deployServer", parameter["name"]);
        Assert.Equal(chosen, parameter["default"]);
        Assert.Contains(chosen, ((List<object>)parameter["allowed_values"]).Cast<string>());
    }

    [Fact]
    public void ServersWithNoChoice_LeaveTheParameterWithoutDefault_WhichIsWhyTheWizardHoldsTheCreationUntilOneIsChosen()
    {
        var production = new PipelineSetupProductionInput("deploy/compose.yml", "replace", null, ["vps-a", "vps-b"]);

        var parameter = Parameter(PipelineSetupYamlBuilder.Build("atlas-deploy-prod", "application-deploy-prod", 3, Input(), production));

        Assert.Equal("true", parameter["required"]);
        Assert.False(parameter.ContainsKey("default"));
    }
}
