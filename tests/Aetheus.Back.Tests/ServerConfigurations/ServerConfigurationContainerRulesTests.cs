// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.ServerConfigurations;
using Aetheus.Back.Data;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Every allow-list rule of the docker section, both where the import validates it and where the
/// `docker run` builder re-checks it before interpolating a field into the command.
/// </summary>
public sealed class ServerConfigurationContainerRulesTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly ServerConfigurationService _sut;

    public ServerConfigurationContainerRulesTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}")
            .Options);
        _sut = new ServerConfigurationService(
            new ServerConfigurationRepository(_db),
            TimeProvider.System,
            Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>());
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ValidateConfigurationAsync_ReportsEveryInvalidDockerField()
    {
        const string yaml = """
            server:
              name: test
              type: Docker
            docker:
              containers:
                - name: "-bad name"
                  image: "nginx;rm"
                  restart: sometimes
                  ports:
                    - "80:80"
                    - "eighty"
                  volumes:
                    - "/data:/data:ro"
                    - "no-target"
                  env:
                    GOOD_KEY: "plain"
                    "1BAD": "x"
                    INJECT: "a;b"
                - name: noimage
              images:
                - nginx:latest
                - "bad image"
              compose_stacks:
                - content: "services: {}"
                - name: "bad stack!"
                  content: "services: {}"
            """;

        var result = await _sut.ValidateConfigurationAsync(yaml, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(
            [
                "Container image 'nginx;rm' contains invalid characters.",
                "Container name '-bad name' contains invalid characters.",
                "Container '-bad name' has invalid restart policy 'sometimes'.",
                "Container '-bad name' has invalid port mapping 'eighty'.",
                "Container '-bad name' has invalid volume mapping 'no-target'.",
                "Container '-bad name' has invalid env key '1BAD'.",
                "Container '-bad name' env 'INJECT' contains shell metacharacters.",
                "Container 'noimage' is missing an image.",
                "Docker image 'bad image' contains invalid characters.",
                "Compose stack name is required.",
                "Compose stack name 'bad stack!' contains invalid characters.",
            ],
            result.Errors);
    }

    [Fact]
    public async Task ValidateConfigurationAsync_AcceptsEveryAllowedDockerField()
    {
        const string yaml = """
            server:
              name: test
              type: Docker
            docker:
              containers:
                - name: web.1
                  image: registry.example/app:1.2@sha256
                  restart: unless-stopped
                  ports: ["8080:80/tcp", "53/udp"]
                  volumes: ["/srv/data:/data:rw"]
                  env:
                    MODE: "production with spaces"
            """;

        var result = await _sut.ValidateConfigurationAsync(yaml, TestContext.Current.CancellationToken);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    [Fact]
    public void BuildDockerRunCommand_QuotesEnvValuesAndKeepsTheImageLast()
    {
        var command = ServerConfigurationService.BuildDockerRunCommand(new ServerConfigContainer
        {
            Name = "web",
            Image = "nginx:1.27",
            Restart = "always",
            Ports = ["80:80", "443:443/tcp"],
            Volumes = ["/srv/www:/usr/share/nginx/html:ro"],
            Env = new Dictionary<string, string> { ["GREETING"] = "hello world" }
        });

        Assert.Equal(
            "docker run -d --name web --restart always -p 80:80 -p 443:443/tcp "
            + "-v /srv/www:/usr/share/nginx/html:ro -e GREETING='hello world' nginx:1.27",
            command);
    }

    [Fact]
    public async Task AnEnvEntryWithoutValue_PassesValidationAndBuildsAnEmptyVariable()
    {
        const string yaml = """
            server:
              name: test
              type: Docker
            docker:
              containers:
                - name: web
                  image: nginx
                  env:
                    EMPTY:
            """;

        var result = await _sut.ValidateConfigurationAsync(yaml, TestContext.Current.CancellationToken);
        var container = YamlParsingHelper.ServerConfigDeserializer.Deserialize<ServerConfigYaml>(yaml)
            .Docker!.Containers[0];

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Equal("docker run -d --name web -e EMPTY='' nginx", ServerConfigurationService.BuildDockerRunCommand(container));
    }

    [Fact]
    public void BuildDockerRunCommand_WithoutRestartPolicy_OmitsTheFlag()
    {
        var command = ServerConfigurationService.BuildDockerRunCommand(
            new ServerConfigContainer { Name = "worker", Image = "busybox" });

        Assert.Equal("docker run -d --name worker busybox", command);
    }

    public static TheoryData<string, ServerConfigContainer, string> UnsafeContainers() => new()
    {
        { "name", new ServerConfigContainer { Name = "web;id", Image = "nginx" }, "Container name 'web;id'" },
        { "image", new ServerConfigContainer { Name = "web", Image = "nginx && id" }, "Container image 'nginx && id'" },
        { "restart", new ServerConfigContainer { Name = "web", Image = "nginx", Restart = "always;id" }, "invalid restart policy" },
        { "port", new ServerConfigContainer { Name = "web", Image = "nginx", Ports = ["80 -v /:/host"] }, "invalid port mapping" },
        { "volume", new ServerConfigContainer { Name = "web", Image = "nginx", Volumes = ["/:/host --privileged"] }, "invalid volume mapping" },
        { "env key", new ServerConfigContainer { Name = "web", Image = "nginx", Env = new() { ["A B"] = "x" } }, "invalid env key 'A B'" },
        { "env value", new ServerConfigContainer { Name = "web", Image = "nginx", Env = new() { ["A"] = "x' -v /:/h '" } }, "env 'A' contains shell metacharacters" },
    };

    [Theory]
    [MemberData(nameof(UnsafeContainers))]
    public void BuildDockerRunCommand_RefusesEveryFieldThatCouldInjectArguments(
        string field, ServerConfigContainer container, string expected)
    {
        var error = Assert.Throws<BadRequestException>(() => ServerConfigurationService.BuildDockerRunCommand(container));

        Assert.True(error.Message.Contains(expected, StringComparison.Ordinal), $"{field}: {error.Message}");
    }
}
