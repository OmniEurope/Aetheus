// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>ADR-057: which file of a repository is a Compose file, which one the wizard proposes for production, and
/// which paths the deployment accepts.</summary>
public sealed class ProductionComposeFilesTests
{
    [Theory]
    [InlineData("compose.yaml", true)]
    [InlineData("docker-compose.yml", true)]
    [InlineData("remote.compose.yml", true)]
    [InlineData("compose.json", false)]
    [InlineData("Dockerfile", false)]
    [InlineData("readme.yml", false)]
    public void ComposeFiles_AreRecognizedByTheirNameAndExtension(string name, bool expected) =>
        Assert.Equal(expected, ProductionComposeFiles.IsComposeFile(name));

    [Fact]
    public void AmongSeveralFiles_TheOneThatSaysProductionWins_AndNoDevelopmentFileIsEverProposed()
    {
        string[] files =
        [
            "deploy/compose/dev-db.compose.yml", "deploy/compose/remote.compose.yml", "docker-compose.override.yml", "compose.yaml"
        ];

        Assert.Equal("deploy/compose/remote.compose.yml", ProductionComposeFiles.Choose(files));
    }

    [Fact]
    public void WithNoProductionName_ThePlainFileIsProposed_AndWhenOnlyDevelopmentFilesExist_NoneIs()
    {
        Assert.Equal("compose.yaml", ProductionComposeFiles.Choose(["compose.yaml", "docker-compose.dev.yml"]));
        Assert.Null(ProductionComposeFiles.Choose(["docker-compose.dev.yml", "docker-compose.override.yml"]));
        Assert.Null(ProductionComposeFiles.Choose(["docker-compose.dev.yml"]));
        Assert.Null(ProductionComposeFiles.Choose([]));
    }

    [Fact]
    public void ASingleFile_IsTheAnswer_WhateverItsDirectory() =>
        Assert.Equal("ops/stack/compose.yml", ProductionComposeFiles.Choose(["ops/stack/compose.yml"]));

    [Theory]
    [InlineData("compose.yaml", true)]
    [InlineData("deploy/compose/remote.compose.yml", true)]
    [InlineData("/etc/compose.yaml", false)]
    [InlineData("../compose.yaml", false)]
    [InlineData("deploy/../compose.yaml", false)]
    [InlineData("deploy//compose.yaml", false)]
    [InlineData("deploy\\compose.yaml", false)]
    [InlineData("deploy/script.sh", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ThePathPassedToCompose_StaysInsideTheRepository(string? path, bool expected) =>
        Assert.Equal(expected, ProductionComposeFiles.IsSafePath(path));
}
