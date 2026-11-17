// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// PLAN-002 lot 1.10, recette R3-62 to R3-64 and ADR-050: a project runs the generic templates without
/// adding a single file to its repository. Each shipped generic template is extended exactly as the setup
/// wizard writes a project's pipeline (<c>extends: name@N</c>, variables, no stage of its own), resolved
/// by the real <see cref="PipelineTemplateResolver"/> and validated; the resolved definition must then
/// name no script of the repository and no scanner configuration file the repository would have to carry.
/// </summary>
public sealed class GenericTemplatesWithoutProjectFilesTests
{
    private const int OrganizationId = 3;

    /// <summary>A run that supplies the one required parameter (the candidate) and overrides no other: each takes
    /// its declared default, as the run service resolves it.</summary>
    private static readonly Dictionary<string, string> NoRunParameters = new() { ["candidateVersion"] = "1.0.0" };

    public static TheoryData<string, string> GenericTemplates()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, definition) in DeliveryTemplateCatalog.Definitions)
            foreach (var resource in definition.Resources.Where(item => item.StartsWith("generic/", StringComparison.Ordinal)))
                data.Add(name, resource);
        return data;
    }

    [Theory]
    [MemberData(nameof(GenericTemplates))]
    public async Task ProjectPipelineExtendingTheTemplate_ResolvesAndValidates_WithNoRepositoryFile(
        string templateName, string resource)
    {
        var templateYaml = DeliveryTemplateCatalog.ReadResource(resource);
        var resolver = new PipelineTemplateResolver(Repository(templateName, templateYaml));

        var resolution = await resolver.ResolveAsync(
            ProjectPipeline(templateName), OrganizationId, ct: TestContext.Current.CancellationToken);

        var definition = resolution.Definition;
        var errors = new List<string>();
        var warnings = new List<string>();
        PipelineDefinitionValidator.ValidateDefinitionBasics(definition, errors, warnings);
        PipelineDefinitionValidator.ValidateStages(definition, errors, warnings);
        Assert.Empty(errors);
        Assert.Equal(templateName, resolution.TemplateName);
        Assert.Equal(1, resolution.ResolvedVersion);

        // The project adds no stage: the run is the template's, stage for stage (the template as it resolves, a
        // deployment template holding its core's stages).
        var template = (await resolver.ResolveAsync(templateYaml, OrganizationId, ct: TestContext.Current.CancellationToken)).Definition;
        Assert.Equal(template.Stages.Select(stage => stage.Name), definition.Stages.Select(stage => stage.Name));

        var stages = YamlParsingHelper.FlattenJobs(definition);
        var shells = stages.SelectMany(stage => stage.Steps).Select(step => step.Shell ?? string.Empty).ToList();
        Assert.All(shells, shell => Assert.DoesNotContain(".pipeline/scripts/", shell, StringComparison.Ordinal));
        Assert.All(shells, shell => Assert.DoesNotContain("deploy/scripts/", shell, StringComparison.Ordinal));
        Assert.All(shells, shell => Assert.DoesNotContain("test -f", shell, StringComparison.Ordinal));
        Assert.Empty(PipelineSetupReadinessService.RequiredScannerFiles(stages));
    }

    /// <summary>The project's own commands are declared in its pipeline, never in a file: every
    /// template step that runs project work reads one <c>APPLICATION_*_COMMANDS</c> variable, and the
    /// project's value reaches the resolved definition. Templates with no reliable default for any stack
    /// declare it empty (recette R2-25: the wizard asks for no command, the template runs what it has).</summary>
    [Theory]
    [InlineData("application-ci", "generic/application-ci.yaml", "APPLICATION_CI_COMMANDS", false)]
    [InlineData("application-quality", "generic/application-quality.yaml", "APPLICATION_QUALITY_COMMANDS", false)]
    [InlineData("application-qa", "generic/application-qa.yaml", "APPLICATION_QA_COMMANDS", false)]
    [InlineData("application-qa-extended", "generic/application-qa-extended.yaml", "APPLICATION_QA_EXTENDED_COMMANDS", true)]
    [InlineData("application-deploy-prod", "generic/application-deploy-prod.yaml", "APPLICATION_DEPLOY_PROD_COMMANDS", false)]
    [InlineData("application-release-fast", "generic/application-release-fast.yaml", "APPLICATION_RELEASE_FAST_COMMANDS", false)]
    [InlineData("package-telemetry", "generic/package-telemetry.yaml", "APPLICATION_PACKAGE_TELEMETRY_COMMANDS", true)]
    [InlineData("package-web-analytics-dotnet", "generic/package-web-analytics-dotnet.yaml", "APPLICATION_PACKAGE_DOTNET_ANALYTICS_COMMANDS", true)]
    [InlineData("package-web-analytics-browser", "generic/package-web-analytics-browser.yaml", "APPLICATION_PACKAGE_BROWSER_ANALYTICS_COMMANDS", true)]
    [InlineData("publish-observability-packages", "generic/publish-observability-packages.yaml", "APPLICATION_PUBLISH_PACKAGES_COMMANDS", true)]
    public async Task ProjectCommands_AreAPipelineVariable_TheTemplateRuns(
        string templateName, string resource, string variable, bool declaredEmpty)
    {
        var templateYaml = DeliveryTemplateCatalog.ReadResource(resource);
        var resolver = new PipelineTemplateResolver(Repository(templateName, templateYaml));
        var template = (await resolver.ResolveAsync(templateYaml, OrganizationId, ct: TestContext.Current.CancellationToken)).Definition;
        var project = ProjectPipeline(templateName) + $"\n  {variable}: \"make release\"";

        var resolved = (await resolver.ResolveAsync(
            project, OrganizationId, ct: TestContext.Current.CancellationToken)).Definition;

        Assert.Equal(declaredEmpty, template.Variables[variable].Length == 0);
        Assert.Equal("make release", resolved.Variables[variable]);
        var runner = Assert.Single(
            YamlParsingHelper.FlattenJobs(resolved).SelectMany(stage => stage.Steps),
            step => (step.Shell ?? string.Empty).Contains($"sh -eu -c \"${variable}\"", StringComparison.Ordinal));
        Assert.Contains($"${{{variable}:-}}", runner.Shell, StringComparison.Ordinal);
    }

    /// <summary>R2-1 and R2-25: the pipeline the wizard writes for a .NET project carries no command, and the
    /// CI, quality and QA templates still resolve it to one, so the project runs without typing any. The
    /// commands name nothing of the platform's own repository (ADR-054) and call no script of the project's
    /// (ADR-050).</summary>
    [Theory]
    [InlineData("application-ci", "generic/application-ci.yaml", "APPLICATION_CI_COMMANDS")]
    [InlineData("application-quality", "generic/application-quality.yaml", "APPLICATION_QUALITY_COMMANDS")]
    [InlineData("application-qa", "generic/application-qa.yaml", "APPLICATION_QA_COMMANDS")]
    public async Task WizardPipeline_WithNoCommand_ResolvesToTheDotNetDefault(
        string templateName, string resource, string variable)
    {
        var templateYaml = DeliveryTemplateCatalog.ReadResource(resource);
        var resolver = new PipelineTemplateResolver(Repository(templateName, templateYaml));

        var resolved = (await resolver.ResolveAsync(
            ProjectPipeline(templateName), OrganizationId, ct: TestContext.Current.CancellationToken)).Definition;

        var commands = resolved.Variables[variable];
        Assert.NotEmpty(commands);
        Assert.Contains("dotnet", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("aetheus", commands, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pipeline/", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("deploy/scripts/", commands, StringComparison.Ordinal);
        // A $(NAME) reference of the pipeline syntax in the default would be substituted before the shell
        // sees it: the default holds only shell command substitutions (lower case).
        Assert.DoesNotContain(PipelineCommandBuilder.ReferencedVariableNames(commands),
            name => name.All(character => char.IsAsciiLetterUpper(character) || character == '_'));
    }

    /// <summary>The three defaults hand work to each other through fixed paths: the quality default writes
    /// the report the quality template publishes, and the QA default reads the source archive the CI default
    /// seals in the payload. A change on one side alone breaks the chain without any step failing early.</summary>
    [Fact]
    public async Task TheDotNetDefaults_AgreeOnTheFilesTheyExchange()
    {
        var ci = await ResolvedVariablesAsync("application-ci", "generic/application-ci.yaml");
        var quality = await ResolvedVariablesAsync("application-quality", "generic/application-quality.yaml");
        var qa = await ResolvedVariablesAsync("application-qa", "generic/application-qa.yaml");

        Assert.Contains(".pipeline-artifacts/source.tar.gz", ci["APPLICATION_CI_COMMANDS"], StringComparison.Ordinal);
        Assert.Contains(".pipeline-artifacts/source.tar.gz", qa["APPLICATION_QA_COMMANDS"], StringComparison.Ordinal);
        var publishedReport = Assert.Single(
            YamlParsingHelper.FlattenJobs(YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
                DeliveryTemplateCatalog.ReadResource("generic/application-quality.yaml"))!)
                .SelectMany(stage => stage.Steps), step => step.Type == "coverage").TargetFiles;
        Assert.Contains(".pipeline-quality/coverage.cobertura.xml", publishedReport);
        Assert.Contains(".pipeline-quality/coverage.cobertura.xml", quality["APPLICATION_QUALITY_COMMANDS"], StringComparison.Ordinal);
    }

    /// <summary>Recette R2-9: the production deployment has a working default, Docker Compose on the target
    /// server, so a project whose setup wizard created the production pipeline deploys without typing a command.
    /// Like the other defaults it names nothing of the platform's own repository (ADR-054), calls no script of
    /// the project's (ADR-050), and holds only shell substitutions. A project that declares its own commands
    /// replaces it whole (see <see cref="ProjectCommands_AreAPipelineVariable_TheTemplateRuns"/>).</summary>
    [Fact]
    public async Task ProductionDeployment_DefaultsToDockerCompose_OnTheTargetServer()
    {
        var deploy = await ResolvedVariablesAsync("application-deploy-prod", "generic/application-deploy-prod.yaml");

        var commands = deploy["APPLICATION_DEPLOY_PROD_COMMANDS"];
        Assert.NotEmpty(commands);
        Assert.Contains("docker compose", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("aetheus", commands, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pipeline/", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("deploy/scripts/", commands, StringComparison.Ordinal);
        Assert.DoesNotContain(PipelineCommandBuilder.ReferencedVariableNames(commands),
            name => name.All(character => char.IsAsciiLetterUpper(character) || character == '_'));
    }

    /// <summary>Recette R2-9 / R2-28 / R-21: the production deployment is bound to a server by the template's
    /// <c>deployServer</c> parameter, which the setup wizard writes as a choice among the project's servers. Every
    /// stage that works on the candidate's workspace (it restores the payload, then verifies and deploys it) must
    /// land on that one server, or the restored files would be on another machine than the command. The parameter
    /// is required: a run without a server is refused before it starts, never handed to any runner (the unresolved
    /// literal it left made the W5 deployment wait for a runner that never existed).</summary>
    [Theory]
    [InlineData("application-deploy-prod")]
    [InlineData("application-deploy-prod-front-back")]
    public async Task DeployServerParameter_BindsEveryStageThatSharesTheCandidateWorkspace(string template)
    {
        var resolver = new PipelineTemplateResolver(Repository(template, DeliveryTemplateCatalog.ReadResource(
            DeliveryTemplateCatalog.Definitions[template].Resources.Single())));
        var bound = $"""
            name: demo-{template}
            extends: {template}@1
            trigger: manual
            stages: []
            parameters:
              - name: deployServer
                display_name: Production server
                type: string
                required: false
                default: "atlas-prod"
            variables:
              APPLICATION_PREFIX: "demo"
            """;

        var boundStages = YamlParsingHelper.FlattenJobs(
            (await resolver.ResolveAsync(bound, OrganizationId, NoRunParameters, TestContext.Current.CancellationToken)).Definition);
        var unbound = await Assert.ThrowsAsync<BadRequestException>(() =>
            resolver.ResolveAsync(ProjectPipeline(template), OrganizationId, NoRunParameters, TestContext.Current.CancellationToken));

        static bool ShareTheWorkspace(PipelineStageDefinition stage) =>
            stage.Steps.Any(step => step.Type == "restore-artifacts"
                || (step.Shell ?? string.Empty).Contains("$APPLICATION_DEPLOY_PROD_COMMANDS", StringComparison.Ordinal)
                || step.Function == "verify-release-ancestry");
        var onTheWorkspace = boundStages.Where(ShareTheWorkspace).ToList();
        Assert.True(onTheWorkspace.Count >= 3, "The restore, the verification and the deployment share one workspace.");
        Assert.All(onTheWorkspace, stage => Assert.Equal("atlas-prod", stage.Agent));
        Assert.Contains("deployServer", unbound.Message, StringComparison.Ordinal);
    }

    /// <summary>R-11: whether the API has its own host is the template a pipeline extends, never a switch. The monolith
    /// template deploys one host (the front's own, the interface and the API served together); the front-and-API template
    /// adds the API's host to its three steps that name hosts. Everything else is the common core's, stage for stage,
    /// with the server the stages run on and the stage they wait for kept (a stage a template writes again replaces the
    /// core's field for field).</summary>
    [Fact]
    public async Task TheTwoDeploymentTemplates_DifferOnlyByTheHostsTheyApply_AndShareTheCoreStageForStage()
    {
        var monolithWithItsGuard = await ResolveDeploymentAsync("application-deploy-prod");
        var frontBack = await ResolveDeploymentAsync("application-deploy-prod-front-back");

        // The monolith alone has the one stage that refuses a strategy other than `replace`; the rest is the core's.
        Assert.Equal(StrategyGuard, Assert.Single(monolithWithItsGuard.Stages, stage => !frontBack.Stages.Any(other => other.Name == stage.Name)).Name);
        var monolith = monolithWithItsGuard with { Stages = [.. monolithWithItsGuard.Stages.Where(stage => stage.Name != StrategyGuard)] };
        Assert.Equal(monolith.Stages.Select(stage => stage.Name), frontBack.Stages.Select(stage => stage.Name));
        Assert.All(monolith.Stages.Zip(frontBack.Stages), pair =>
        {
            Assert.Equal(pair.First.Agent, pair.Second.Agent);
            Assert.Equal(pair.First.DependsOn.Where(name => name != StrategyGuard), pair.Second.DependsOn);
            Assert.Equal(pair.First.Group, pair.Second.Group);
            Assert.Equal(pair.First.ExecutionRole, pair.Second.ExecutionRole);
            Assert.Equal(pair.First.Os, pair.Second.Os);
            Assert.Equal(pair.First.Steps.Select(step => step.Name), pair.Second.Steps.Select(step => step.Name));
        });
        Assert.All(frontBack.Stages.Where(stage => stage.Name is "Apply HTTP vhosts" or "TLS" or "Apply Apache vhosts"),
            stage => Assert.Equal("atlas-prod", stage.Agent));

        var onlyTheFront = HostFiles(monolith);
        var frontAndApi = HostFiles(frontBack);
        Assert.Equal(["$(APP_HOST).conf", "$(APP_HOST)-ssl.conf"], onlyTheFront);
        Assert.Equal(["$(APP_HOST).conf", "$(API_HOST).conf", "$(APP_HOST)-ssl.conf", "$(API_HOST)-ssl.conf"], frontAndApi);
        Assert.Equal(
            ["$(APP_HOST) $(API_HOST)", "$(APP_HOST)"],
            new[] { frontBack, monolith }.Select(definition => Assert.Single(
                definition.Stages.SelectMany(stage => stage.Steps), step => step.Function == "ensure-tls-certificates").With["hosts"]));
        // No switch is left: the shape is the template, not a variable the steps read.
        Assert.All(new[] { monolith, frontBack }, definition =>
        {
            Assert.DoesNotContain("DEPLOY_HAS_API", definition.Variables.Keys);
            Assert.DoesNotContain(definition.Stages.SelectMany(stage => stage.Steps),
                step => (step.Condition ?? string.Empty).Contains("DEPLOY_HAS_API", StringComparison.Ordinal));
        });
    }

    private const string StrategyGuard = "Check the strategy";

    /// <summary>R-11: a monolith is deployed by replacement only, but only the wizard enforced it. A pipeline written by hand
    /// with another strategy must be refused by the run itself: the guard is the first stage (every other stage waits for
    /// it, so nothing reaches the server), and its step, run with the pipeline's variables as the agent runs it, exits
    /// non-zero with an explicit message for anything but `replace`. The shell is really run, not read.</summary>
    [Theory]
    [InlineData("replace", true)]
    [InlineData("bluegreen", false)]
    [InlineData("", false)]
    public async Task AMonolithPipeline_WithAStrategyOtherThanReplace_IsRefusedByItsFirstStage(string strategy, bool accepted)
    {
        var template = "application-deploy-prod";
        var resolver = new PipelineTemplateResolver(Repository(template, DeliveryTemplateCatalog.ReadResource(
            DeliveryTemplateCatalog.Definitions[template].Resources.Single())));
        var handWritten = $"""
            name: demo-{template}
            extends: {template}@1
            trigger: manual
            stages: []
            variables:
              APPLICATION_PREFIX: "demo"
              DEPLOY_STRATEGY: "{strategy}"
            """;
        var parameters = new Dictionary<string, string> { ["candidateVersion"] = "1.0.0", ["deployServer"] = "atlas-prod" };

        var definition = (await resolver.ResolveAsync(
            handWritten, OrganizationId, parameters, TestContext.Current.CancellationToken)).Definition;

        var guard = Assert.Single(definition.Stages, stage => stage.Name == StrategyGuard);
        Assert.Empty(guard.DependsOn);
        Assert.All(definition.Stages.Where(stage => stage != guard), stage => Assert.True(
            WaitsFor(definition, stage, StrategyGuard), $"'{stage.Name}' must wait for the strategy check."));

        var (exitCode, error) = await RunShellAsync(Assert.Single(guard.Steps).Shell!, definition.Variables["DEPLOY_STRATEGY"]);
        Assert.Equal(accepted ? 0 : 1, exitCode);
        if (accepted) return;
        Assert.Contains("replace", error, StringComparison.Ordinal);
        Assert.Contains("front-back", error, StringComparison.Ordinal);
        Assert.Contains($"'{strategy}'", error, StringComparison.Ordinal);
    }

    /// <summary>The front-and-API template keeps blue-green: it carries no such guard.</summary>
    [Fact]
    public async Task TheFrontAndApiTemplate_HasNoStrategyGuard_AsItSupportsBlueGreen() =>
        Assert.DoesNotContain((await ResolveDeploymentAsync("application-deploy-prod-front-back")).Stages,
            stage => stage.Name == StrategyGuard);

    private static bool WaitsFor(PipelineYamlDefinition definition, PipelineStageDefinition stage, string name) =>
        stage.DependsOn.Any(dependency => dependency == name
            || WaitsFor(definition, definition.Stages.Single(other => other.Name == dependency), name));

    private static async Task<(int ExitCode, string Error)> RunShellAsync(string script, string strategy)
    {
        var start = new System.Diagnostics.ProcessStartInfo(
            OperatingSystem.IsWindows() ? @"C:\Program Files\Git\bin\bash.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.Environment["DEPLOY_STRATEGY"] = strategy;
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await error);
    }

    private static List<string> HostFiles(PipelineYamlDefinition definition) => [.. definition.Stages
        .SelectMany(stage => stage.Steps)
        .Where(step => step.Type == "apache-config")
        .SelectMany(step => step.ConfigFiles.Keys)];

    private static async Task<PipelineYamlDefinition> ResolveDeploymentAsync(string template)
    {
        var resolver = new PipelineTemplateResolver(Repository(template, DeliveryTemplateCatalog.ReadResource(
            DeliveryTemplateCatalog.Definitions[template].Resources.Single())));
        var parameters = new Dictionary<string, string> { ["candidateVersion"] = "1.0.0", ["deployServer"] = "atlas-prod" };
        return (await resolver.ResolveAsync(ProjectPipeline(template), OrganizationId, parameters,
            TestContext.Current.CancellationToken)).Definition;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ResolvedVariablesAsync(string templateName, string resource)
    {
        var resolver = new PipelineTemplateResolver(Repository(templateName, DeliveryTemplateCatalog.ReadResource(resource)));
        return (await resolver.ResolveAsync(
            ProjectPipeline(templateName), OrganizationId, ct: TestContext.Current.CancellationToken)).Definition.Variables;
    }

    /// <summary>Without commands, the steps whose work only the project knows refuse to run rather than
    /// report a QA, a deployment or a package that never happened (AGENTS.md, no fake success).</summary>
    [Theory]
    [InlineData("generic/application-qa.yaml", "APPLICATION_QA_COMMANDS")]
    [InlineData("generic/application-deploy-prod-core-v1.yaml", "APPLICATION_DEPLOY_PROD_COMMANDS")]
    [InlineData("generic/application-release-fast.yaml", "APPLICATION_RELEASE_FAST_COMMANDS")]
    [InlineData("generic/package-telemetry.yaml", "APPLICATION_PACKAGE_TELEMETRY_COMMANDS")]
    [InlineData("generic/package-web-analytics-dotnet.yaml", "APPLICATION_PACKAGE_DOTNET_ANALYTICS_COMMANDS")]
    [InlineData("generic/package-web-analytics-browser.yaml", "APPLICATION_PACKAGE_BROWSER_ANALYTICS_COMMANDS")]
    [InlineData("generic/publish-observability-packages.yaml", "APPLICATION_PUBLISH_PACKAGES_COMMANDS")]
    public void WithoutCommands_ProjectWorkFailsAndSaysWhy(string resource, string variable)
    {
        var shell = CommandStep(resource, variable).Shell!;

        var guard = shell.IndexOf($"if test -z \"${{{variable}:-}}\"; then", StringComparison.Ordinal);
        var refusal = shell.IndexOf("exit 1", StringComparison.Ordinal);
        var run = shell.IndexOf($"sh -eu -c \"${variable}\"", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < refusal && refusal < run, $"{resource} must refuse before it runs.");
        Assert.Contains($"set {variable} in this pipeline.", shell, StringComparison.Ordinal);
    }

    /// <summary>Recette R-67 and R-59: the browser tests and the extended QA are declared by the project (or by the setup
    /// wizard for it), and a pipeline that declares none must not fail by construction: the run warns, in the user's
    /// language through a named run warning, and succeeds. <c>none</c> is a declared choice: it runs nothing, says so, and
    /// raises no warning. A command is still run, whole, with <c>sh -eu -c</c>.</summary>
    [Theory]
    [InlineData("generic/application-qa.yaml", "APPLICATION_QA_E2E_COMMANDS", "QaE2eCommandMissing")]
    [InlineData("generic/application-qa-extended.yaml", "APPLICATION_QA_EXTENDED_COMMANDS", "QaExtendedCommandMissing")]
    public void WithoutDeclaredCommands_TheRunWarnsAndSucceeds_AndNoneIsADeclaredChoice(
        string resource, string variable, string warningCode)
    {
        var shell = CommandStep(resource, variable).Shell!;

        var empty = shell.IndexOf($"if test -z \"${{{variable}:-}}\"; then", StringComparison.Ordinal);
        var warning = shell.IndexOf($"##aetheus[setvariable name=RUN_WARNING_CODE]{warningCode}", StringComparison.Ordinal);
        var firstExit = shell.IndexOf("exit 0", empty, StringComparison.Ordinal);
        var none = shell.IndexOf($"if test \"${variable}\" = none; then", StringComparison.Ordinal);
        var run = shell.IndexOf($"sh -eu -c \"${variable}\"", StringComparison.Ordinal);
        Assert.True(empty >= 0 && empty < warning && warning < firstExit, $"{resource}: an undeclared command must warn, then succeed.");
        Assert.True(firstExit < none && none < run, $"{resource}: `none` must be read before the command is run.");
        Assert.DoesNotContain("exit 1", shell[..run], StringComparison.Ordinal);
        Assert.Contains($"{variable}): ", shell[empty..none], StringComparison.Ordinal);
        Assert.DoesNotContain("RUN_WARNING", shell[none..run], StringComparison.Ordinal);
    }

    private static PipelineStepDefinition CommandStep(string resource, string variable) =>
        Assert.Single(
            YamlParsingHelper.FlattenJobs(YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
                DeliveryTemplateCatalog.ReadResource(resource))!).SelectMany(stage => stage.Steps),
            step => (step.Shell ?? string.Empty).Contains($"\"${variable}\"", StringComparison.Ordinal));

    /// <summary>The pipeline the setup wizard writes for a project: the template, its prefix, no stage.</summary>
    private static string ProjectPipeline(string templateName) => $"""
        name: demo-{templateName}
        extends: {templateName}@1
        trigger: manual
        stages: []
        variables:
          APPLICATION_PREFIX: "demo"
        """;

    /// <summary>The shipped generic templates, each at its version 1 (the catalog's one file), with <paramref name="yaml"/>
    /// as the one of <paramref name="name"/>: a deployment template names its core, which the resolver then finds beside it.</summary>
    private static IPipelineTemplateRepository Repository(string name, string yaml)
    {
        var repository = Substitute.For<IPipelineTemplateRepository>();
        var shipped = DeliveryTemplateCatalog.Definitions
            .Where(entry => entry.Value.Resources.All(resource => resource.StartsWith("generic/", StringComparison.Ordinal)))
            .ToDictionary(entry => entry.Key, entry => DeliveryTemplateCatalog.ReadResource(entry.Value.Resources.Single()));
        shipped[name] = yaml;
        var id = 40;
        foreach (var (templateName, templateYaml) in shipped)
        {
            var template = new PipelineTemplate
            {
                Id = ++id,
                Name = templateName,
                OrganizationId = OrganizationId,
                LatestVersion = 1,
                Versions = [new PipelineTemplateVersion { Version = 1, YamlContent = templateYaml }]
            };
            repository.FindTemplateByNameAsync(templateName, OrganizationId, Arg.Any<CancellationToken>()).Returns(template);
        }
        return repository;
    }
}
