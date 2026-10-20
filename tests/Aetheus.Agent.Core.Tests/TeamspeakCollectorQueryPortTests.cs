// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The ServerQuery port of a running TeamSpeak server is read from the <c>ts3server.ini</c> beside the binary
/// <c>which ts3server</c> found, and falls back to 10011 when that file is absent, says nothing, or the path
/// cannot be used. The ini is a real file in a temporary directory.
/// </summary>
public sealed class TeamspeakCollectorQueryPortTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aetheus-ts3-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("default_voice_port=9987\nQuery_Port = 10022\n", 10022)]
    [InlineData("default_voice_port=9987\n", 10011)]
    [InlineData(null, 10011)]
    public async Task TheQueryPort_IsReadFromTheIniBesideTheBinary_Or10011(string? ini, int expected)
    {
        if (ini is not null)
            await File.WriteAllTextAsync(Path.Combine(_dir, "ts3server.ini"), ini, TestContext.Current.CancellationToken);

        var dto = await Collect(_dir.Replace('\\', '/') + "/ts3server");

        Assert.True(dto.IsRunning);
        Assert.Equal(expected, dto.QueryPort);
    }

    [Fact]
    public async Task ABinaryPathWithoutADirectory_FallsBackTo10011()
    {
        var dto = await Collect("ts3server");

        Assert.True(dto.IsRunning);
        Assert.Equal(10011, dto.QueryPort);
    }

    // Running (pidof answers), binary found by `which` only, no ServerQuery credential: the collection stops
    // after the port is known, so the port is what it reports.
    private static Task<TeamspeakDataDto> Collect(string whichOutput)
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(1, string.Empty, string.Empty));
        shell.RunExecAsync("pidof", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "771", string.Empty));
        shell.RunExecAsync("which", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, whichOutput + "\n", string.Empty));
        var collector = new TeamspeakCollector(
            NullLogger<TeamspeakCollector>.Instance, shell, Substitute.For<ITeamspeakQueryClient>(), fileExists: _ => false);
        return collector.CollectAsync(TestContext.Current.CancellationToken);
    }
}
