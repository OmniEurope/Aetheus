// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Covers <see cref="PipelineVariableResolver.ResolveFullVariablesForRunAsync"/> - the per-run
/// variable set a pipeline run executes against. Libraries/vaults are empty here so the test stays
/// focused on the system + project + additional variable plumbing (and the $(VAR) substitution pass).
/// </summary>
public class PipelineVariableResolverTests
{
    private readonly IVariableLibraryService _varLib = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vault = Substitute.For<IVaultService>();
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly PipelineVariableResolver _sut;

    public PipelineVariableResolverTests()
        => _sut = new PipelineVariableResolver(
            _varLib, _vault, _repo, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System);

    private static PipelineRun NewRun(
        Project? project, string additionalVarsJson = "{}", string? commitHash = null, int buildNumber = 0) => new()
        {
            Id = 42,
            PipelineId = 7,
            CommitHash = commitHash,
            BuildNumber = buildNumber,
            AdditionalVariablesJson = additionalVarsJson,
            Pipeline = new Pipeline { Id = 7, Name = "build", ProjectId = project is null ? null : 1, Project = project }
        };

    [Fact]
    public async Task ResolveFullVariablesForRunAsync_InjectsSystemAndProjectVariables()
    {
        var run = NewRun(new Project { Name = "App", RepositoryUrl = "https://repo", DefaultBranch = "develop", OrganizationId = 1 });
        var def = new PipelineYamlDefinition { Name = "build", Trigger = "manual" };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);

        Assert.Equal("42", vars["BUILD_BUILDID"]);
        Assert.Equal("20042", vars["BUILD_RUN_PORT"]);
        Assert.Equal("7", vars["BUILD_PIPELINEID"]);
        Assert.Equal("1", vars["BUILD_PROJECTID"]);
        Assert.Equal("App", vars["BUILD_PROJECTNAME"]);
        Assert.Equal("https://repo", vars["REPOSITORY_URL"]);
        Assert.Equal("develop", vars["DEFAULT_BRANCH"]);
        Assert.Equal("true", vars["CI"]);
    }

    [Fact]
    public async Task ResolveFullVariablesForRunAsync_ExposesPerPipelineBuildNumberDistinctFromRunId()
    {
        var run = NewRun(project: null, buildNumber: 3);
        var def = new PipelineYamlDefinition { Name = "build", Trigger = "manual" };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);

        // The run id is globally monotonic and jumps between runs of the same pipeline; the build
        // number is the per-pipeline sequence a version pattern should use.
        Assert.Equal("3", vars["BUILD_PIPELINE_RUNNUMBER"]);
        Assert.Equal("42", vars["BUILD_BUILDID"]);
    }

    [Fact]
    public async Task ResolveFullVariablesForRunAsync_SubstitutesBuildNumberIntoVersionPattern()
    {
        var run = NewRun(project: null, buildNumber: 12);
        var def = new PipelineYamlDefinition
        {
            Name = "build",
            Trigger = "manual",
            Variables = new Dictionary<string, string> { ["APP_VERSION"] = "1.1.$(BUILD_PIPELINE_RUNNUMBER)" }
        };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);

        Assert.Equal("1.1.12", vars["APP_VERSION"]);
    }

    /// <summary> Every stage is resolved again from the run, so the release number the run was
    /// created with reaches each one (app-version.sh composes 1.3.<number>), and a run without one
    /// reads 0. </summary>
    [Fact]
    public async Task ResolveFullVariablesForRunAsync_ExposesTheRunReleaseNumberToEveryStage()
    {
        var run = NewRun(project: null, buildNumber: 12);
        run.ReleaseNumber = 4;
        var def = new PipelineYamlDefinition
        {
            Name = "build",
            Variables = new Dictionary<string, string> { ["APP_VERSION"] = "1.3.$(BUILD_RELEASENUMBER)" }
        };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);
        var withoutNumber = await _sut.ResolveFullVariablesForRunAsync(
            NewRun(project: null), new PipelineYamlDefinition { Name = "build" }, TestContext.Current.CancellationToken);

        Assert.Equal("4", vars["BUILD_RELEASENUMBER"]);
        Assert.Equal("1.3.4", vars["APP_VERSION"]);
        Assert.Equal("0", withoutNumber["BUILD_RELEASENUMBER"]);
    }

    [Fact]
    public async Task ResolveFullVariablesForRunAsync_RunBranchOverridesProjectBranchEverywhere()
    {
        var run = NewRun(new Project
        {
            Name = "App",
            RepositoryUrl = "https://repo",
            DefaultBranch = "main",
            OrganizationId = 1
        });
        run.BranchName = "develop";

        var vars = await _sut.ResolveFullVariablesForRunAsync(
            run, new PipelineYamlDefinition { Name = "nightly" }, TestContext.Current.CancellationToken);

        Assert.Equal("develop", vars["DEFAULT_BRANCH"]);
        Assert.Equal("develop", vars["BUILD_SOURCEBRANCH"]);
    }

    [Fact]
    public async Task ResolveFullVariablesForRunAsync_ExposesProjectType_WhenDeclared()
    {
        // S-TECH-53: a declared project_type surfaces as PROJECT_TYPE; absent when not declared.
        var run = NewRun(new Project { Name = "App", OrganizationId = 1 });
        var withType = await _sut.ResolveFullVariablesForRunAsync(
            run, new PipelineYamlDefinition { Name = "build", ProjectType = "dotnet" }, TestContext.Current.CancellationToken);
        Assert.Equal("dotnet", withType["PROJECT_TYPE"]);

        var withoutType = await _sut.ResolveFullVariablesForRunAsync(
            run, new PipelineYamlDefinition { Name = "build" }, TestContext.Current.CancellationToken);
        Assert.False(withoutType.ContainsKey("PROJECT_TYPE"));
    }

    [Fact]
    public async Task ResolveFullVariablesForRunAsync_MergesAdditionalVariables()
    {
        var run = NewRun(project: null, additionalVarsJson: "{\"MY_VAR\":\"hello\"}");
        var def = new PipelineYamlDefinition { Name = "build", Trigger = "manual" };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);

        Assert.Equal("hello", vars["MY_VAR"]);
    }

    [Fact]
    public async Task ResolveFullVariablesForRunAsync_ReinjectsPinnedSourceRevisionAfterPrepare()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var run = NewRun(project: null, additionalVarsJson: "{\"BUILD_SOURCEVERSION\":\"untrusted\"}", commitHash: commit);
        var def = new PipelineYamlDefinition { Name = "build", Trigger = "manual" };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);

        Assert.Equal(commit, vars["BUILD_SOURCEVERSION"]);
    }

    [Fact]
    public async Task ResolveFullVariablesForRunAsync_SubstitutesNestedTokens()
    {
        var run = NewRun(new Project { Name = "App", DefaultBranch = "main", OrganizationId = 1 });
        var def = new PipelineYamlDefinition
        {
            Name = "build",
            Trigger = "manual",
            Variables = new Dictionary<string, string> { ["TAG"] = "v-$(BUILD_BUILDID)" }
        };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);

        Assert.Equal("v-42", vars["TAG"]);
    }

    [Fact]
    public async Task ResolveFullVariablesForRunAsync_NormalizesEmptyYamlVariable()
    {
        var run = NewRun(new Project { Name = "App", DefaultBranch = "main", OrganizationId = 1 });
        var def = new PipelineYamlDefinition
        {
            Name = "build",
            Trigger = "manual",
            Variables = new Dictionary<string, string> { ["PACKAGE_DIGEST"] = null! }
        };

        var vars = await _sut.ResolveFullVariablesForRunAsync(
            run, def, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, vars["PACKAGE_DIGEST"]);
    }

    [Fact]
    public async Task ResolveFullVariablesForRunAsync_MapsDeclaredParameterIntoVariable()
    {
        var run = NewRun(project: null, additionalVarsJson: "{\"runSmokeChecks\":\"true\",\"parameters.runSmokeChecks\":\"true\"}");
        var def = new PipelineYamlDefinition
        {
            Name = "release",
            Trigger = "manual",
            Parameters =
            [
                new PipelineTemplateParameterDefinition
                {
                    Name = "runSmokeChecks",
                    Type = "boolean",
                    Default = "false"
                }
            ],
            Variables = new Dictionary<string, string>
            {
                ["AETHEUS_RUN_SMOKE_CHECKS"] = "$(runSmokeChecks)"
            }
        };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);

        Assert.Equal("true", vars["runSmokeChecks"]);
        Assert.Equal("true", vars["AETHEUS_RUN_SMOKE_CHECKS"]);
    }

    /// <summary>Variable fallbacks allow absent capabilities to resolve to explicit defaults rather
    /// than literal references.</summary>
    [Fact]
    public async Task ResolveFullVariablesForRunAsync_AppliesADefaultForANameNothingProvides()
    {
        var run = NewRun(project: null, buildNumber: 12);
        var def = new PipelineYamlDefinition
        {
            Name = "release-fast",
            Trigger = "manual",
            Variables = new Dictionary<string, string>
            {
                ["APP_VERSION"] = "1.1.$(RELEASE_COUNTER:-$(BUILD_PIPELINE_RUNNUMBER))",
                ["CHANNEL"] = "$(RELEASE_CHANNEL:-stable)",
                ["EXPLICIT"] = "$(BUILD_PIPELINE_RUNNUMBER:-0)"
            }
        };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);

        Assert.Equal("stable", vars["CHANNEL"]);
        Assert.Equal("12", vars["EXPLICIT"]);
        // One substitution does not nest a default, but resolution expands in passes: the inner
        // reference resolves first, then the outer default applies. That is the release-fast case -
        // a new counter with the per-pipeline run number as its fallback.
        Assert.Equal("1.1.12", vars["APP_VERSION"]);
    }

    /// <summary>
    /// A default on a name some step publishes later must NOT be applied at resolution: it would be
    /// baked in before the real value exists. It stays pending and is resolved once outputs are known.
    /// </summary>
    [Fact]
    public async Task ResolveFullVariablesForRunAsync_KeepsADefaultPendingForANameAStepPublishes()
    {
        var run = NewRun(project: null);
        var def = new PipelineYamlDefinition
        {
            Name = "deploy",
            Trigger = "manual",
            Variables = new Dictionary<string, string> { ["BROWSER_IMAGE"] = "smoke:$(SOURCE_COMMIT:-latest)" },
            Stages =
            [
                new PipelineStageDefinition
                {
                    Name = "Prepare",
                    Steps = [new PipelineStepDefinition { Name = "prepare", Shell = "sh prepare.sh", Outputs = ["SOURCE_COMMIT"] }]
                }
            ]
        };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);

        Assert.Equal("smoke:$(SOURCE_COMMIT:-latest)", vars["BROWSER_IMAGE"]);
    }

    /// <summary>Declaring script-published names under outputs makes them available to stage
    /// resolution.</summary>
    [Fact]
    public async Task ResolveFullVariablesForRunAsync_AcceptsAReferenceToADeclaredOutput()
    {
        var run = NewRun(project: null);
        var def = new PipelineYamlDefinition
        {
            Name = "deploy",
            Trigger = "manual",
            Variables = new Dictionary<string, string> { ["BROWSER_IMAGE"] = "smoke:$(SOURCE_COMMIT)" },
            Stages =
            [
                new PipelineStageDefinition
                {
                    Name = "Prepare",
                    Steps = [new PipelineStepDefinition { Name = "prepare", Shell = "sh prepare.sh", Outputs = ["SOURCE_COMMIT"] }]
                }
            ]
        };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, def, TestContext.Current.CancellationToken);

        Assert.Equal("smoke:$(SOURCE_COMMIT)", vars["BROWSER_IMAGE"]);
    }

    [Fact]
    public async Task DropResolvedLibraryWarningsAsync_DropsOnlyTheLibraryThatNowExists()
    {
        // A run records its warnings at launch. The library named in the first was created afterwards,
        // while the run was still going; the second still does not exist, and neither does the third
        // warning name a library at all.
        _varLib.ResolveLibrariesWithCrossAccessAndNamesAsync(
                Arg.Any<List<string>>(), 1, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aetheus-prod-host" }));

        var current = await _sut.DropResolvedLibraryWarningsAsync(
            [
                "Variable library 'aetheus-prod-host' not found.",
                "Variable library 'still-missing' not found.",
                "Vault 'prod' not found."
            ],
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Variable library 'still-missing' not found.", "Vault 'prod' not found."],
            current);
    }

    [Fact]
    public async Task DropResolvedLibraryWarningsAsync_WithoutALibraryWarning_AsksNothing()
    {
        var current = await _sut.DropResolvedLibraryWarningsAsync(
            ["Vault 'prod' not found."], 1, TestContext.Current.CancellationToken);

        Assert.Equal(["Vault 'prod' not found."], current);
        await _varLib.DidNotReceive().ResolveLibrariesWithCrossAccessAndNamesAsync(
            Arg.Any<List<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _varLib.DidNotReceive().ResolveLibrariesWithNamesAsync(
            Arg.Any<List<string>>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DropResolvedLibraryWarningsAsync_StillMissing_KeepsTheWarning()
    {
        _varLib.ResolveLibrariesWithCrossAccessAndNamesAsync(
                Arg.Any<List<string>>(), 1, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        var current = await _sut.DropResolvedLibraryWarningsAsync(
            ["Variable library 'aetheus-prod-host' not found."], 1, TestContext.Current.CancellationToken);

        Assert.Equal(["Variable library 'aetheus-prod-host' not found."], current);
    }

    [Fact]
    public async Task DropResolvedLibraryWarningsAsync_WithoutAProject_UsesTheProjectlessLookup()
    {
        _varLib.ResolveLibrariesWithNamesAsync(
                Arg.Any<List<string>>(), null, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shared" }));

        var current = await _sut.DropResolvedLibraryWarningsAsync(
            ["Variable library 'shared' not found."], null, TestContext.Current.CancellationToken);

        Assert.Empty(current);
    }
}
