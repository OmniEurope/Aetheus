// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Recette R-4: a step is never handed an external address to clone through the project variables, only
/// Aetheus's own clone URL of the project mirror, re-homed onto the configured clone base.</summary>
public sealed class PipelineProjectVariablesExternalAddressTests
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GitLight:CloneBaseUrl"] = "http://aetheus-back:8080" })
        .Build();

    [Fact]
    public void Inject_ExternalAddressWithoutMirror_GivesNoRepositoryUrl()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        PipelineProjectVariables.Inject(vars, new Project { Name = "OE", RepositoryUrl = "https://github.com/team/app.git" }, Config);

        Assert.Equal(string.Empty, vars["REPOSITORY_URL"]);
        Assert.Equal(string.Empty, vars["BUILD_REPOSITORY_URI"]);
    }

    [Fact]
    public void Inject_MirrorCloneUrl_GivesTheReHomedMirror()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        PipelineProjectVariables.Inject(vars, new Project { Name = "OE", RepositoryUrl = "http://localhost:5300/git/7/app.git" }, Config);

        Assert.Equal("http://aetheus-back:8080/git/7/app.git", vars["REPOSITORY_URL"]);
        Assert.Equal("http://aetheus-back:8080/git/7/app.git", vars["BUILD_REPOSITORY_URI"]);
    }
}
