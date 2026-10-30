// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-32: the launch-time check of the configuration templates a run will render.</summary>
public class PipelineConfigTemplatePreflightTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private readonly IPipelineGitService _git = Substitute.For<IPipelineGitService>();
    private readonly IProjectConfigRepositoryService _configRepository = Substitute.For<IProjectConfigRepositoryService>();

    private const string Yaml = """
        name: deploy
        trigger: manual
        stages:
          - name: prepare
            agent: linux-01
            steps:
              - name: compute ports
                shell: ./ports.sh
                outputs: [FRONT_PORT]
          - name: expose
            agent: linux-01
            depends_on: [prepare]
            variables:
              STAGE_ONLY: x
            steps:
              - name: vhosts
                type: apache-config
                config_files:
                  "site.conf": ".pipeline/configs/apache/site.conf"
              - name: proxy
                type: apache-proxy
                server_name: app.example.test
                upstream: http://127.0.0.1:8080
        """;

    private PipelineConfigTemplatePreflight CreateSut() => new(_git, _configRepository);

    private static PipelineYamlDefinition Definition() => YamlParsingHelper.ParseAndValidate(Yaml)!;

    private void ArrangeTemplates(string site, string proxy)
    {
        _configRepository.ReadAsync(7, ".pipeline/configs/apache/site.conf", Commit, 11, Arg.Any<CancellationToken>())
            .Returns(site);
        _git.ReadProjectConfigAtRevisionAsync(
                7, PipelineHostOperationTaskFactory.DefaultProxyTemplatePath, Commit, Arg.Any<CancellationToken>(), 11)
            .Returns(proxy);
    }

    [Fact]
    public async Task FindProblemsAsync_TokensTheRunProvides_NoProblem()
    {
        // A launch variable, a later step's declared output, a stage variable, the names the proxy step
        // and the stage dispatch inject: all of them exist by the time the template is rendered.
        ArrangeTemplates(
            "ServerName #{APP_HOST}#\nProxyPass / http://127.0.0.1:#{FRONT_PORT}#/ #{STAGE_ONLY}# #{SYSTEM_STAGENAME}#\n",
            "ServerName #{AETHEUS_APACHE_SERVER_NAME}#\nProxyPass / #{AETHEUS_APACHE_UPSTREAM}#/\n");

        var findings = await CreateSut().FindProblemsAsync(
            Definition(), new Dictionary<string, string> { ["APP_HOST"] = "app.example.test" },
            7, Commit, 11, TestContext.Current.CancellationToken);

        Assert.Empty(findings.Problems);
    }

    [Fact]
    public async Task FindProblemsAsync_TokenNothingDefines_NamesTheTemplateTheStepAndTheToken()
    {
        ArrangeTemplates(
            "ServerName #{APP_HOST}#\nServerAlias #{API_HOST_ALIAS}#\n",
            "ServerName #{AETHEUS_APACHE_SERVER_NAME}#\n");

        var findings = await CreateSut().FindProblemsAsync(
            Definition(), new Dictionary<string, string> { ["APP_HOST"] = "app.example.test" },
            7, Commit, 11, TestContext.Current.CancellationToken);

        var found = Assert.Single(findings.Problems);
        Assert.Equal(PipelineProblemCodes.ConfigTemplateUndefinedVariables, found.Code);
        Assert.Equal("'API_HOST_ALIAS'", found.Parameters["variables"]);
        var problem = found.English;
        Assert.Contains(".pipeline/configs/apache/site.conf", problem, StringComparison.Ordinal);
        Assert.Contains("step 'vhosts'", problem, StringComparison.Ordinal);
        Assert.Contains("'API_HOST_ALIAS'", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("'APP_HOST'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindProblemsAsync_NoImmutableRevision_ReadsNothing()
    {
        var findings = await CreateSut().FindProblemsAsync(
            Definition(), new Dictionary<string, string>(), 7, "main", 11, TestContext.Current.CancellationToken);

        Assert.Empty(findings.Problems);
        await _configRepository.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, default, TestContext.Current.CancellationToken);
        await _git.DidNotReceiveWithAnyArgs().ReadProjectConfigAtRevisionAsync(default, default!, default!, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task FindProblemsAsync_MissingTemplates_RefuseTheLaunchBeforeAnyStageRuns()
    {
        // R-82: neither the configuration repository nor the source commit holds the vhost, and the project
        // has no proxy template: the dispatch would fail both steps, after the earlier stages had touched the host.
        _configRepository.ReadAsync(7, ".pipeline/configs/apache/site.conf", Commit, 11, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _git.ReadProjectConfigAtRevisionAsync(
                7, PipelineHostOperationTaskFactory.DefaultProxyTemplatePath, Commit, Arg.Any<CancellationToken>(), 11)
            .Returns((string?)null);

        var findings = await CreateSut().FindProblemsAsync(
            Definition(), new Dictionary<string, string>(), 7, Commit, 11, TestContext.Current.CancellationToken);

        Assert.Equal(2, findings.Problems.Count);
        var vhostProblem = Assert.Single(findings.Problems, problem =>
            problem.English.Contains(".pipeline/configs/apache/site.conf", StringComparison.Ordinal));
        Assert.Equal(PipelineProblemCodes.ConfigTemplateMissingInConfigRepository, vhostProblem.Code);
        var vhost = vhostProblem.English;
        Assert.Contains("step 'vhosts'", vhost, StringComparison.Ordinal);
        Assert.Contains("found neither in the project's configuration repository nor at commit " + Commit, vhost, StringComparison.Ordinal);
        Assert.Contains(findings.Problems, problem =>
            problem.English.Contains(PipelineHostOperationTaskFactory.DefaultProxyTemplatePath, StringComparison.Ordinal)
            && problem.English.Contains("step 'proxy'", StringComparison.Ordinal)
            && problem.Code == PipelineProblemCodes.ConfigTemplateMissingAtCommit);
        Assert.Empty(findings.Warnings);
    }

    private const string ConditionalYaml = """
        name: deploy
        trigger: manual
        variables:
          DEPLOY_HAS_API: "false"
        stages:
          - name: http
            agent: linux-01
            steps:
              - name: front vhost
                type: apache-config
                condition: "ne(variables['DEPLOY_HAS_API'], 'true')"
                config_files:
                  "front.conf": ".pipeline/configs/apache/front.conf"
              - name: front and api vhosts
                type: apache-config
                condition: "eq(variables['DEPLOY_HAS_API'], 'true')"
                config_files:
                  "api.conf": ".pipeline/configs/apache/api.conf"
              - name: late vhost
                type: apache-config
                condition: "eq(variables['LATE_FLAG'], 'yes')"
                config_files:
                  "late.conf": ".pipeline/configs/apache/late.conf"
              - name: rollback vhost
                type: apache-config
                condition: "failed()"
                config_files:
                  "rollback.conf": ".pipeline/configs/apache/rollback.conf"
          - name: flag
            agent: linux-01
            depends_on: [http]
            steps:
              - name: set flag
                shell: echo "##aetheus[setvariable name=LATE_FLAG]yes"
        """;

    [Fact]
    public async Task FindProblemsAsync_MissingTemplate_OnlyRefusesWhatRunsOnTheLaunchVariables()
    {
        // Every template is absent. Only the step certain to run refuses; the API step is off on this launch,
        // the late one depends on a value a step publishes, and the rollback step only runs after a failure.
        _configRepository.ReadAsync(7, Arg.Any<string>(), Commit, 11, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var findings = await CreateSut().FindProblemsAsync(
            YamlParsingHelper.ParseAndValidate(ConditionalYaml)!, new Dictionary<string, string> { ["DEPLOY_HAS_API"] = "false" },
            7, Commit, 11, TestContext.Current.CancellationToken);

        var refusal = Assert.Single(findings.Problems).English;
        Assert.Contains("front.conf", refusal, StringComparison.Ordinal);
        Assert.Contains("step 'front vhost'", refusal, StringComparison.Ordinal);
        var warningProblem = Assert.Single(findings.Warnings);
        Assert.Equal(PipelineProblemCodes.ConfigTemplateMissingInConfigRepositoryMayRun, warningProblem.Code);
        var warning = warningProblem.English;
        Assert.Contains("late.conf", warning, StringComparison.Ordinal);
        Assert.Contains("only known once the run is under way", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindProblemsAsync_ApiLaunch_RefusesTheApiTemplateInsteadOfTheFrontOne()
    {
        _configRepository.ReadAsync(7, Arg.Any<string>(), Commit, 11, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var findings = await CreateSut().FindProblemsAsync(
            YamlParsingHelper.ParseAndValidate(ConditionalYaml)!, new Dictionary<string, string> { ["DEPLOY_HAS_API"] = "true" },
            7, Commit, 11, TestContext.Current.CancellationToken);

        var refusal = Assert.Single(findings.Problems).English;
        Assert.Contains("api.conf", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("front.conf", refusal, StringComparison.Ordinal);
    }
}
