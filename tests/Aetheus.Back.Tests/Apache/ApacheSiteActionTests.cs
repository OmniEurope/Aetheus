// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Apache;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Recette R-12: enabling, disabling and deleting a site queue a typed agent operation whose target is
/// the bare site file name; production refused the old <c>a2dissite x.conf &amp;&amp; systemctl reload</c> shell
/// string ("No allow-list pattern matched segment").</summary>
public class ApacheSiteActionTests
{
    private readonly IApacheRepository _repo = Substitute.For<IApacheRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly Aetheus.Back.Components.Tasks.ITaskService _taskService = Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
    private readonly ApacheService _sut;
    private ServerTask? _queued;

    public ApacheSiteActionTests()
    {
        _sut = new ApacheService(_repo, _audit, _taskService);
        _repo.AddTaskAsync(Arg.Do<ServerTask>(task => _queued = task), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    [Theory]
    [InlineData(ApacheAction.EnableSite, OperationKind.ApacheEnableSite)]
    [InlineData(ApacheAction.DisableSite, OperationKind.ApacheDisableSite)]
    [InlineData(ApacheAction.DeleteSite, OperationKind.ApacheDeleteSite)]
    public async Task SiteAction_QueuesTheTypedOperation_OnTheSiteFile_NeverAShellCommand(ApacheAction action, OperationKind expected)
    {
        await _sut.ExecuteActionAsync(7, new ApacheActionRequest { Action = action, TargetName = "aetheus-api.sonytumen.com-ssl.conf" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(_queued);
        Assert.Equal(ExecutorType.Operation, _queued.Executor);
        Assert.Equal(expected, _queued.Operation);
        Assert.Equal("aetheus-api.sonytumen.com-ssl.conf", _queued.Command);
        Assert.DoesNotContain("a2", _queued.Command, StringComparison.Ordinal);
        Assert.True(OperationTargetValidator.IsValid(expected, _queued.Command));
        await _audit.Received(1).LogAsync($"Apache{action}", "Apache", 7, "aetheus-api.sonytumen.com-ssl.conf", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("../sites-enabled/x.conf")]
    [InlineData("/etc/apache2/sites-available/x.conf")]
    [InlineData("..")]
    [InlineData(".hidden.conf")]
    [InlineData("site")]
    [InlineData("site.conf; rm -rf /")]
    [InlineData("sub\\site.conf")]
    [InlineData("site.conf\nother.conf")]
    [InlineData("site.conf\n")]
    public async Task SiteAction_RefusesAnythingButABareConfFileName(string name)
    {
        foreach (var action in new[] { ApacheAction.EnableSite, ApacheAction.DisableSite, ApacheAction.DeleteSite })
        {
            await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(
                1, new ApacheActionRequest { Action = action, TargetName = name }, TestContext.Current.CancellationToken));
        }
        Assert.Null(_queued);
    }

    [Theory]
    [InlineData("example.com.conf")]
    [InlineData("000-default.conf")]
    [InlineData("api.aetheus.sonytumen.com-le-ssl.conf")]
    [InlineData("Site_1.conf")]
    public async Task DeleteSite_AcceptsOrdinarySiteFileNames(string name)
    {
        await _sut.ExecuteActionAsync(1, new ApacheActionRequest { Action = ApacheAction.DeleteSite, TargetName = name },
            TestContext.Current.CancellationToken);

        Assert.Equal(OperationKind.ApacheDeleteSite, _queued?.Operation);
        Assert.Equal(name, _queued?.Command);
    }
}
