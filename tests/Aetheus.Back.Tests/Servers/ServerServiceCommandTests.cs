// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests.Servers;

/// <summary>The command line queued for a service action, per service type: the executor that will
/// run it matters as much as the text, and a type the manager does not know is refused.</summary>
public sealed class ServerServiceCommandTests
{
    [Theory]
    [InlineData(ServiceAction.Start, "systemctl start nginx")]
    [InlineData(ServiceAction.Stop, "systemctl stop nginx")]
    [InlineData(ServiceAction.Restart, "systemctl restart nginx")]
    public void Systemd_UsesSystemctlThroughTheShellExecutor(ServiceAction action, string expected)
    {
        var (command, executor) = ServerServiceManager.BuildServiceCommand(action, "nginx", ServiceType.Systemd);

        Assert.Equal(expected, command);
        Assert.Equal(ExecutorType.Shell, executor);
    }

    [Theory]
    [InlineData(ServiceAction.Start, "docker start web")]
    [InlineData(ServiceAction.Stop, "docker stop web")]
    [InlineData(ServiceAction.Restart, "docker restart web")]
    public void Docker_UsesTheDockerExecutor(ServiceAction action, string expected)
    {
        var (command, executor) = ServerServiceManager.BuildServiceCommand(action, "web", ServiceType.Docker);

        Assert.Equal(expected, command);
        Assert.Equal(ExecutorType.Docker, executor);
    }

    [Theory]
    [InlineData(ServiceAction.Start, "net start \"Spooler\"")]
    [InlineData(ServiceAction.Stop, "net stop \"Spooler\"")]
    [InlineData(ServiceAction.Restart, "net stop \"Spooler\" && net start \"Spooler\"")]
    public void WindowsService_QuotesTheNameAndRestartsAsStopThenStart(ServiceAction action, string expected)
    {
        var (command, executor) = ServerServiceManager.BuildServiceCommand(action, "Spooler", ServiceType.WindowsService);

        Assert.Equal(expected, command);
        Assert.Equal(ExecutorType.Shell, executor);
    }

    [Fact]
    public void UnknownServiceType_IsRefused()
    {
        var exception = Assert.Throws<BadRequestException>(() =>
            ServerServiceManager.BuildServiceCommand(ServiceAction.Start, "x", (ServiceType)99));

        Assert.Contains("Unsupported service type", exception.Message, StringComparison.Ordinal);
    }
}
