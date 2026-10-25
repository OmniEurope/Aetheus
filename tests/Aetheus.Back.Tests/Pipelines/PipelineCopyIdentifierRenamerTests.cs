// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The identifiers a copy renames from the source project to the new one are the ones its
/// YAML structure identifies (a bare identifier in <c>variables:</c>, an environment name). Host
/// names, paths, pinned runners and shell text stay as written.</summary>
public sealed class PipelineCopyIdentifierRenamerTests
{
    [Fact]
    public void Rename_RewritesBareIdentifierVariables_AndEnvironmentNames()
    {
        const string yaml = """
            name: ats-deploy
            variables:
              COMPOSE_PROJECT_NAME: aetheus-prod
              STACK: "aetheus"
              SNAKE: aetheus_blue   # keeps the comment
              HOST: aetheus.example.com
              IMAGE: ghcr.io/org/aetheus-back
              PATH_ROOT: /opt/aetheus/app
              OTHER: other-aetheus
            requires:
              environments:
                - aetheus-prod
            stages:
              - name: Deploy
                environment: aetheus-staging
                agent: aetheus-runner-1
                steps: []
            """;

        var (renamed, found) = PipelineCopyIdentifierRenamer.Rename(yaml, "aetheus", "ats");

        Assert.Contains("COMPOSE_PROJECT_NAME: ats-prod", renamed, StringComparison.Ordinal);
        Assert.Contains("STACK: \"ats\"", renamed, StringComparison.Ordinal);
        Assert.Contains("SNAKE: ats_blue   # keeps the comment", renamed, StringComparison.Ordinal);
        Assert.Contains("- ats-prod", renamed, StringComparison.Ordinal);
        Assert.Contains("environment: ats-staging", renamed, StringComparison.Ordinal);
        // Left untouched: addresses, image references, paths, a runner pinned by name, a value that
        // merely contains the prefix.
        Assert.Contains("HOST: aetheus.example.com", renamed, StringComparison.Ordinal);
        Assert.Contains("IMAGE: ghcr.io/org/aetheus-back", renamed, StringComparison.Ordinal);
        Assert.Contains("PATH_ROOT: /opt/aetheus/app", renamed, StringComparison.Ordinal);
        Assert.Contains("agent: aetheus-runner-1", renamed, StringComparison.Ordinal);
        Assert.Contains("OTHER: other-aetheus", renamed, StringComparison.Ordinal);
        Assert.Equal(
        [
            "COMPOSE_PROJECT_NAME: aetheus-prod -> ats-prod",
            "STACK: aetheus -> ats",
            "SNAKE: aetheus_blue -> ats_blue",
            "environment: aetheus-prod -> ats-prod",
            "environment: aetheus-staging -> ats-staging"
        ], found);
    }

    [Fact]
    public void Rename_NeverReadsABlockScalar()
    {
        const string yaml = """
            variables:
              KEEP: aetheus-x
            stages:
              - name: Build
                steps:
                  - name: Compose
                    shell: |
                      cat <<EOF
                      environment: aetheus-prod
                      EOF
                    working_directory: aetheus-dir
            """;

        var (renamed, found) = PipelineCopyIdentifierRenamer.Rename(yaml, "aetheus", "ats");

        Assert.Contains("environment: aetheus-prod", renamed, StringComparison.Ordinal);
        Assert.Contains("KEEP: ats-x", renamed, StringComparison.Ordinal);
        Assert.Equal("KEEP: aetheus-x -> ats-x", Assert.Single(found));
    }

    [Fact]
    public void Rename_OnlyTouchesVariablesInsideAVariablesMapping()
    {
        const string yaml = """
            parameters:
              - name: target
                default: aetheus-prod
            labels:
              COMPOSE_PROJECT_NAME: aetheus-prod
            variables:
              A: aetheus-a
            after:
              B: aetheus-b
            """;

        var (renamed, found) = PipelineCopyIdentifierRenamer.Rename(yaml, "aetheus", "ats");

        Assert.Contains("default: aetheus-prod", renamed, StringComparison.Ordinal);
        Assert.Contains("COMPOSE_PROJECT_NAME: aetheus-prod", renamed, StringComparison.Ordinal);
        Assert.Contains("A: ats-a", renamed, StringComparison.Ordinal);
        Assert.Contains("B: aetheus-b", renamed, StringComparison.Ordinal);
        Assert.Equal("A: aetheus-a -> ats-a", Assert.Single(found));
    }

    [Fact]
    public void Rename_KeepsCrLfLineEndings()
    {
        var (renamed, _) = PipelineCopyIdentifierRenamer.Rename(
            "variables:\r\n  A: aetheus-a\r\n  B: keep\r\n", "aetheus", "ats");

        Assert.Equal("variables:\r\n  A: ats-a\r\n  B: keep\r\n", renamed);
    }

    [Theory]
    [InlineData("", "ats")]
    [InlineData("aetheus", "")]
    [InlineData("aetheus", "aetheus")]
    public void Rename_DoesNothingWithoutADistinctPairOfPrefixes(string sourcePrefix, string prefix)
    {
        const string yaml = "variables:\n  A: aetheus-a\n";

        var (renamed, found) = PipelineCopyIdentifierRenamer.Rename(yaml, sourcePrefix, prefix);

        Assert.Equal(yaml, renamed);
        Assert.Empty(found);
    }
}
