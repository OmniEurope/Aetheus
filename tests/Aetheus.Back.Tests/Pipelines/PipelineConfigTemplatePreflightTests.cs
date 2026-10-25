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

        var problems = await CreateSut().FindProblemsAsync(
            Definition(), new Dictionary<string, string> { ["APP_HOST"] = "app.example.test" },
            7, Commit, 11, TestContext.Current.CancellationToken);

        Assert.Empty(problems);
    }

    [Fact]
    public async Task FindProblemsAsync_TokenNothingDefines_NamesTheTemplateTheStepAndTheToken()
    {
        ArrangeTemplates(
            "ServerName #{APP_HOST}#\nServerAlias #{API_HOST_ALIAS}#\n",
            "ServerName #{AETHEUS_APACHE_SERVER_NAME}#\n");

        var problems = await CreateSut().FindProblemsAsync(
            Definition(), new Dictionary<string, string> { ["APP_HOST"] = "app.example.test" },
            7, Commit, 11, TestContext.Current.CancellationToken);

        var problem = Assert.Single(problems);
        Assert.Contains(".pipeline/configs/apache/site.conf", problem, StringComparison.Ordinal);
        Assert.Contains("step 'vhosts'", problem, StringComparison.Ordinal);
        Assert.Contains("'API_HOST_ALIAS'", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("'APP_HOST'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindProblemsAsync_NoImmutableRevision_ReadsNothing()
    {
        var problems = await CreateSut().FindProblemsAsync(
            Definition(), new Dictionary<string, string>(), 7, "main", 11, TestContext.Current.CancellationToken);

        Assert.Empty(problems);
        await _configRepository.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, default, TestContext.Current.CancellationToken);
        await _git.DidNotReceiveWithAnyArgs().ReadProjectConfigAtRevisionAsync(default, default!, default!, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task FindProblemsAsync_UnreadableTemplate_IsLeftToTheDispatch()
    {
        _configRepository.ReadAsync(7, ".pipeline/configs/apache/site.conf", Commit, 11, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _git.ReadProjectConfigAtRevisionAsync(
                7, PipelineHostOperationTaskFactory.DefaultProxyTemplatePath, Commit, Arg.Any<CancellationToken>(), 11)
            .Returns((string?)null);

        var problems = await CreateSut().FindProblemsAsync(
            Definition(), new Dictionary<string, string>(), 7, Commit, 11, TestContext.Current.CancellationToken);

        Assert.Empty(problems);
    }
}
