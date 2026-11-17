// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Docker;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R-12: the Docker page does not adopt on arrival the reads of a few seconds (a log, an inspection, the
/// environment, a listing, a compose file). It recognises them by the names in <see cref="ServerTaskNames"/>, so the
/// names the backend gives those tasks come from there, and an action never carries one of them.
/// </summary>
public sealed class DockerReadTaskNamesTests
{
    private const string ContainerId = "abc123def456";

    private readonly IDockerRepository _repo = Substitute.For<IDockerRepository>();
    private readonly List<string> _names = [];
    private readonly DockerService _sut;

    public DockerReadTaskNamesTests()
    {
        _repo.ServerExistsAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        _repo.AddTaskAsync(Arg.Do<ServerTask>(task => _names.Add(task.Name)), Arg.Any<CancellationToken>());
        _sut = new DockerService(_repo, Substitute.For<IAuditService>(), Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>());
    }

    private static bool IsRead(string name) =>
        ServerTaskNames.DockerReadPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task EachReadOfTheDockerPage_IsNamedByAReadPrefix()
    {
        var ct = TestContext.Current.CancellationToken;

        await _sut.GetContainerLogsAsync(1, new DockerContainerLogsRequest { ContainerId = ContainerId, Tail = 10 }, ct);
        await _sut.InspectContainerAsync(1, ContainerId, ct);
        await _sut.GetContainerEnvVarsAsync(1, ContainerId, ct);
        await _sut.ListContainerFilesAsync(1, new DockerBrowseRequest { ContainerId = ContainerId, Path = "/" }, ct);
        await _sut.GetComposeFileAsync(1, "monitoring", ct);

        Assert.Equal(5, _names.Count);
        Assert.All(_names, name => Assert.True(IsRead(name), $"'{name}' is a read but no DockerReadPrefixes entry matches it."));
        Assert.Contains(ServerTaskNames.DockerLogsPrefix + ContainerId, _names);
        Assert.Contains(DockerTaskNames.ComposeFile("monitoring"), _names);
    }

    [Fact]
    public async Task AnActionOfTheDockerPage_IsNotNamedLikeARead()
    {
        var ct = TestContext.Current.CancellationToken;

        await _sut.ExecuteActionAsync(1, new DockerActionRequest { ContainerId = ContainerId, Action = DockerContainerAction.Stop }, ct);

        var name = Assert.Single(_names);
        Assert.StartsWith("Docker ", name, StringComparison.Ordinal);
        Assert.False(IsRead(name), $"'{name}' is an action: the page must adopt it on arrival.");
    }

    [Fact]
    public async Task ACommandOfTheShellPanel_IsNamedFromTheSharedExecName_AndIsNotARead()
    {
        await _sut.ExecuteShellCommandAsync(1, new DockerExecRequest { ContainerId = ContainerId + "ffff", Command = "ls" },
            TestContext.Current.CancellationToken);

        var name = Assert.Single(_names);
        Assert.Equal(ServerTaskNames.DockerExecPrefix + ContainerId, name);
        Assert.False(IsRead(name), $"'{name}' is a command of the shell panel: it is not one of the reads the page leaves alone.");
    }
}
