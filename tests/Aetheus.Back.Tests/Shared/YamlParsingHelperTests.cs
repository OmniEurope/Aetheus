// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests;

public class YamlParsingHelperTests
{
    [Fact]
    public void ParseVaultNames_WithVaults_ReturnsNames()
    {
        var yaml = """
            name: deploy
            vaults:
              - prod-secrets
              - shared-keys
            stages: []
            """;

        var result = YamlParsingHelper.ParseVaultNames(yaml);

        Assert.Equal(2, result.Count);
        Assert.Contains("prod-secrets", result);
        Assert.Contains("shared-keys", result);
    }

    [Fact]
    public void ParseVaultNames_NoVaultsSection_ReturnsEmpty()
    {
        var yaml = """
            name: deploy
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = YamlParsingHelper.ParseVaultNames(yaml);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseVaultNames_EmptyString_ReturnsEmpty()
    {
        var result = YamlParsingHelper.ParseVaultNames("");

        Assert.Empty(result);
    }

    [Fact]
    public void ParseVaultNames_NullString_ReturnsEmpty()
    {
        var result = YamlParsingHelper.ParseVaultNames(null!);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseVaultNames_WhitespaceOnly_ReturnsEmpty()
    {
        var result = YamlParsingHelper.ParseVaultNames("   \n\n  ");

        Assert.Empty(result);
    }

    [Fact]
    public void ParseVaultNames_QuotedNames_StripsQuotes()
    {
        var yaml = """
            name: deploy
            vaults:
              - "quoted-vault"
              - 'single-quoted'
            stages: []
            """;

        var result = YamlParsingHelper.ParseVaultNames(yaml);

        Assert.Equal(2, result.Count);
        Assert.Contains("quoted-vault", result);
        Assert.Contains("single-quoted", result);
    }

    [Fact]
    public void ParseVaultNames_EmptyItems_SkipsBlank()
    {
        var yaml = """
            name: deploy
            vaults:
              - valid-vault
              -   
              - another-vault
            stages: []
            """;

        var result = YamlParsingHelper.ParseVaultNames(yaml);

        Assert.Equal(2, result.Count);
        Assert.Contains("valid-vault", result);
        Assert.Contains("another-vault", result);
    }

    [Fact]
    public void ParseVaultNames_VaultsSectionEndsAtNextKey_StopsParsing()
    {
        var yaml = """
            vaults:
              - my-vault
            stages:
              - name: build
            """;

        var result = YamlParsingHelper.ParseVaultNames(yaml);

        Assert.Single(result);
        Assert.Equal("my-vault", result[0]);
    }

    [Fact]
    public void ParseVaultNames_CaseInsensitiveKey_ParsesCorrectly()
    {
        var yaml = """
            vaults:
              - case-vault
            stages: []
            """;

        var result = YamlParsingHelper.ParseVaultNames(yaml);

        Assert.Single(result);
        Assert.Equal("case-vault", result[0]);
    }

    // --- ParseAndValidate ---

    [Fact]
    public void ParseAndValidate_ValidYaml_ReturnsDefinition()
    {
        var yaml = """
            name: deploy
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        var result = YamlParsingHelper.ParseAndValidate(yaml);

        Assert.NotNull(result);
        Assert.Equal("deploy", result!.Name);
    }

    [Fact]
    public void ParseAndValidate_UnknownTopLevelAndStepKeys_AreSkipped()
    {
        // A key added for a newer backend must not make the whole definition invalid.
        var yaml = """
            name: deploy
            new_top_level_option: true
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
                    new_step_option: 3
            """;

        var result = YamlParsingHelper.ParseAndValidate(yaml);

        Assert.NotNull(result);
        Assert.Equal("dotnet build", Assert.Single(Assert.Single(result!.Stages).Steps).Shell);
    }

    [Fact]
    public void ParseAndValidate_KnownKeyWithTheWrongShape_ReturnsNull()
    {
        var yaml = """
            name: deploy
            stages:
              - name: build
                steps:
                  name: not-a-list
            """;

        Assert.Null(YamlParsingHelper.ParseAndValidate(yaml));
    }

    [Fact]
    public void ServerConfigDeserializer_StaysStrict_OnAnUnknownKey()
    {
        Assert.ThrowsAny<YamlDotNet.Core.YamlException>(() =>
            YamlParsingHelper.ServerConfigDeserializer.Deserialize<ServerConfigYaml>("unknown_section: 1"));
    }

    [Fact]
    public void ParseAndValidate_InvalidYaml_ReturnsNull()
    {
        var result = YamlParsingHelper.ParseAndValidate("{{invalid yaml: [");

        Assert.Null(result);
    }

    [Fact]
    public void ParseAndValidate_WhitespaceOnly_ReturnsNull()
    {
        // An empty/whitespace-only document deserializes to a null definition rather than throwing -
        // callers (e.g. the webhook branch-filter fail-closed path) rely on this returning null too.
        var result = YamlParsingHelper.ParseAndValidate("   ");

        Assert.Null(result);
    }

    [Fact]
    public void ParseAndValidate_BlankVariableLibrary_ReturnsNull()
    {
        var yaml = """
            name: deploy
            variable_libraries:
              - valid
              - ""
            stages: []
            """;

        var result = YamlParsingHelper.ParseAndValidate(yaml);

        Assert.Null(result);
    }

    [Fact]
    public void ParseAndValidate_BlankVault_ReturnsNull()
    {
        var yaml = """
            name: deploy
            vaults:
              - valid
              - " "
            stages: []
            """;

        var result = YamlParsingHelper.ParseAndValidate(yaml);

        Assert.Null(result);
    }

    [Fact]
    public void ParseAndValidate_WithVaultsAndLibraries_ReturnsDefinition()
    {
        var yaml = """
            name: deploy
            vaults:
              - my-vault
            variable_libraries:
              - my-lib
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: step1
                    shell: echo hi
            """;

        var result = YamlParsingHelper.ParseAndValidate(yaml);

        Assert.NotNull(result);
        Assert.Single(result!.Vaults);
        Assert.Single(result.VariableLibraries);
    }

    // --- FlattenJobs ---

    [Fact]
    public void FlattenJobs_LegacyFormat_PassesThrough()
    {
        var yaml = """
            name: deploy
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;
        var def = YamlParsingHelper.ParseAndValidate(yaml)!;

        var result = YamlParsingHelper.FlattenJobs(def);

        Assert.Single(result);
        Assert.Equal("build", result[0].Name);
    }

    [Fact]
    public void FlattenJobs_ParallelJobs_ShareSameExternalDeps()
    {
        var yaml = """
            name: ci
            stages:
              - name: Build
                agent: linux-01
                jobs:
                  - name: compile
                    steps:
                      - name: step1
                        shell: make build
                  - name: test
                    steps:
                      - name: step1
                        shell: make test
                  - name: lint
                    steps:
                      - name: step1
                        shell: make lint
            """;
        var def = YamlParsingHelper.ParseAndValidate(yaml)!;

        var result = YamlParsingHelper.FlattenJobs(def);

        Assert.Equal(3, result.Count);
        Assert.All(result, s => Assert.Equal("Build", s.Group));
        Assert.All(result, s => Assert.Empty(s.DependsOn));
    }

    [Fact]
    public void FlattenJobs_CrossStageDeps_ResolvesToAllJobs()
    {
        var yaml = """
            name: ci
            stages:
              - name: Build
                agent: linux-01
                jobs:
                  - name: compile
                    steps:
                      - name: step1
                        shell: make build
                  - name: test
                    steps:
                      - name: step1
                        shell: make test
              - name: Deploy
                agent: linux-01
                depends_on:
                  - Build
                jobs:
                  - name: deploy-prod
                    steps:
                      - name: step1
                        shell: make deploy
            """;
        var def = YamlParsingHelper.ParseAndValidate(yaml)!;

        var result = YamlParsingHelper.FlattenJobs(def);

        Assert.Equal(3, result.Count);
        var deploy = result.First(s => s.Name == "deploy-prod");
        Assert.Equal(2, deploy.DependsOn.Count);
        Assert.Contains("compile", deploy.DependsOn);
        Assert.Contains("test", deploy.DependsOn);
    }

    [Fact]
    public void FlattenJobs_JobDependsOnSibling_FormsIntraStageDag()
    {
        var yaml = """
            name: ci
            stages:
              - name: Build
                agent: linux-01
                jobs:
                  - name: compile
                    steps:
                      - name: step1
                        shell: make build
                  - name: package
                    depends_on:
                      - compile
                    steps:
                      - name: step1
                        shell: make package
            """;
        var def = YamlParsingHelper.ParseAndValidate(yaml)!;

        var result = YamlParsingHelper.FlattenJobs(def);

        var compile = result.First(s => s.Name == "compile");
        var package = result.First(s => s.Name == "package");
        Assert.Empty(compile.DependsOn);
        Assert.Contains("compile", package.DependsOn);
    }

    [Fact]
    public void FlattenJobs_JobDependsOnSiblingAndStage_MergesBothDeps()
    {
        var yaml = """
            name: ci
            stages:
              - name: Prep
                agent: linux-01
                jobs:
                  - name: restore
                    steps:
                      - name: step1
                        shell: make restore
              - name: Build
                agent: linux-01
                depends_on:
                  - Prep
                jobs:
                  - name: compile
                    steps:
                      - name: step1
                        shell: make build
                  - name: package
                    depends_on:
                      - compile
                    steps:
                      - name: step1
                        shell: make package
            """;
        var def = YamlParsingHelper.ParseAndValidate(yaml)!;

        var result = YamlParsingHelper.FlattenJobs(def);

        var package = result.First(s => s.Name == "package");
        // Cross-stage dep (restore, the only job of Prep) + intra-stage sibling dep (compile).
        Assert.Contains("restore", package.DependsOn);
        Assert.Contains("compile", package.DependsOn);
    }

    [Fact]
    public void FlattenJobs_JobInheritsStageAgent()
    {
        var yaml = """
            name: ci
            stages:
              - name: Build
                agent: shared-server
                jobs:
                  - name: job-no-agent
                    steps:
                      - name: step1
                        shell: echo hi
                  - name: job-with-agent
                    agent: custom-server
                    steps:
                      - name: step1
                        shell: echo hi
            """;
        var def = YamlParsingHelper.ParseAndValidate(yaml)!;

        var result = YamlParsingHelper.FlattenJobs(def);

        Assert.Equal("shared-server", result[0].Agent);
        Assert.Equal("custom-server", result[1].Agent);
    }

    [Fact]
    public void FlattenJobs_JobInheritsStageExecutionRole_UnlessOverridden()
    {
        var yaml = """
            name: ci
            stages:
              - name: Build
                execution_role: build
                jobs:
                  - name: inherited-role
                    steps:
                      - name: compile
                        shell: dotnet build
                  - name: explicit-role
                    execution_role: deploy
                    steps:
                      - name: deploy
                        shell: docker compose up -d
            """;
        var definition = YamlParsingHelper.ParseAndValidate(yaml)!;

        var result = YamlParsingHelper.FlattenJobs(definition);

        Assert.Equal("build", result[0].ExecutionRole);
        Assert.Equal("deploy", result[1].ExecutionRole);
    }

    [Fact]
    public void FlattenJobs_JobInheritsStageOs_UnlessOverridden()
    {
        var yaml = """
            name: ci
            stages:
              - name: Build
                os: linux
                jobs:
                  - name: job-inherits
                    steps:
                      - name: step1
                        shell: echo hi
                  - name: job-overrides
                    os: windows
                    steps:
                      - name: step1
                        shell: echo hi
            """;
        var def = YamlParsingHelper.ParseAndValidate(yaml)!;

        var result = YamlParsingHelper.FlattenJobs(def);

        Assert.Equal("linux", result[0].Os);
        Assert.Equal("windows", result[1].Os);
    }

    [Fact]
    public void FlattenJobs_LegacyStageOs_PassesThrough()
    {
        var yaml = """
            name: deploy
            stages:
              - name: build
                os: windows
                agent: win-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;
        var def = YamlParsingHelper.ParseAndValidate(yaml)!;

        var result = YamlParsingHelper.FlattenJobs(def);

        Assert.Single(result);
        Assert.Equal("windows", result[0].Os);
    }
}
