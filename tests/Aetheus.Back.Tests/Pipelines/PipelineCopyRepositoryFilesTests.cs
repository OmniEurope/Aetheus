// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>A copy cannot carry the files of the source repository its pipelines use, so they are
/// detected and named; URLs, absolute paths, bare file names and comments are not repository files.</summary>
public sealed class PipelineCopyRepositoryFilesTests
{
    [Fact]
    public void Find_NamesTheRepositoryFilesADefinitionUses()
    {
        const string yaml = """
            name: x
            stages:
              - name: Deploy
                steps:
                  - name: Run
                    shell: |
                      sh .pipeline/scripts/ci.sh
                      bash ./deploy/scripts/render.sh --out $(WORKSPACE)/deploy/scripts/render.sh
                      docker compose -f deploy/compose/remote.compose.yml --env-file deploy/compose/.env up
                      docker build -f deploy/docker/Dockerfile.back .
                      node deploy/scripts/nightly-evidence.mjs
                    config_files:
                      /etc/apache2/sites-available/app.conf: .pipeline/configs/apache/aetheus-app-https.conf
            """;

        var found = PipelineCopyRepositoryFiles.Find(yaml);

        Assert.Equal(
        [
            ".pipeline/scripts/ci.sh",
            "deploy/scripts/render.sh",
            "deploy/compose/remote.compose.yml",
            "deploy/compose/.env",
            "deploy/docker/Dockerfile.back",
            "deploy/scripts/nightly-evidence.mjs",
            ".pipeline/configs/apache/aetheus-app-https.conf"
        ], found);
    }

    [Fact]
    public void Find_IgnoresUrlsAbsolutePathsBareNamesImagesAndComments()
    {
        const string yaml = """
            # sh .pipeline/scripts/commented.sh
            name: x
            variables:
              URL: https://example.com/releases/app.json
              ABS: /etc/apache2/conf/site.conf
              BARE: render.sh
              IMAGE: ghcr.io/org/app:1.2
              GIT: git@host.example:org/repo.git
              HOME_FILE: $HOME/.config/tool.toml
            """;

        Assert.Empty(PipelineCopyRepositoryFiles.Find(yaml));
    }

    [Fact]
    public void Find_ListsADirectoryReferenceOnce()
    {
        var found = PipelineCopyRepositoryFiles.Find("shell: ls deploy/scripts/ && ls deploy/scripts/");

        Assert.Equal("deploy/scripts/", Assert.Single(found));
    }
}
