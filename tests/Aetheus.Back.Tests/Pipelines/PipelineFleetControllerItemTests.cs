// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>A fleet item is shown only to a reader of the pipeline and, when it follows a template,
/// of that template too.</summary>
public sealed class PipelineFleetControllerItemTests
{
    private readonly IPipelineFleetService _fleet = Substitute.For<IPipelineFleetService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly PipelineFleetController _sut;

    public PipelineFleetControllerItemTests()
    {
        _sut = new PipelineFleetController(_fleet, _authz, Substitute.For<IPipelineTemplateService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "test")) }
            }
        };
    }

    private void CanRead(ResourceType type, int id, bool allowed) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), type, id, Permission.Read, Arg.Any<CancellationToken>()).Returns(allowed);

    [Fact]
    public async Task AReaderOfAnotherPipeline_IsForbiddenBeforeTheItemIsRead()
    {
        CanRead(ResourceType.Pipeline, 3, false);

        var result = await _sut.GetFleetItem(3, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _fleet.DidNotReceiveWithAnyArgs().GetItemAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APipelineWithoutTemplate_IsReturnedToItsReader()
    {
        CanRead(ResourceType.Pipeline, 3, true);
        var item = new PipelineFleetItemDto { PipelineId = 3, TemplateId = null };
        _fleet.GetItemAsync(3, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _sut.GetFleetItem(3, TestContext.Current.CancellationToken);

        Assert.Same(item, Assert.IsType<OkObjectResult>(result.Result).Value);
        await _authz.DidNotReceive().HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.PipelineTemplate,
            Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APipelineFollowingATemplate_NeedsTheTemplateToo(bool canReadTemplate)
    {
        CanRead(ResourceType.Pipeline, 3, true);
        CanRead(ResourceType.PipelineTemplate, 7, canReadTemplate);
        var item = new PipelineFleetItemDto { PipelineId = 3, TemplateId = 7 };
        _fleet.GetItemAsync(3, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _sut.GetFleetItem(3, TestContext.Current.CancellationToken);

        if (canReadTemplate)
            Assert.Same(item, Assert.IsType<OkObjectResult>(result.Result).Value);
        else
            Assert.IsType<ForbidResult>(result.Result);
    }
}
