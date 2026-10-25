// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Recette R-65, R-66: who reads each library and vault is answered only for the libraries, vaults and
/// pipelines the caller can read, so the usage never tells which pipelines read a resource they cannot see.</summary>
public sealed class PipelineResourceUsageControllerTests
{
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IPipelineResourceUsageService _usage = Substitute.For<IPipelineResourceUsageService>();
    private readonly PipelineResourceUsageController _sut;

    public PipelineResourceUsageControllerTests()
    {
        _usage.GetAsync(Arg.Any<List<int>?>(), Arg.Any<List<int>?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new ResourceUsageDto());
        _sut = new PipelineResourceUsageController(_usage, _authz)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity("test")) } },
        };
    }

    private void Readable(ResourceType type, List<int>? ids) =>
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), type, Permission.Read, Arg.Any<CancellationToken>()).Returns(ids);

    [Fact]
    public async Task NoReadableLibraryNorVault_AnswersEmpty_WithoutReadingAnyPipeline()
    {
        Readable(ResourceType.VariableLibrary, []);
        Readable(ResourceType.Vault, []);

        var result = await _sut.Get(TestContext.Current.CancellationToken);

        var usage = Assert.IsType<ResourceUsageDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(usage.Libraries);
        Assert.Empty(usage.Vaults);
        await _usage.DidNotReceiveWithAnyArgs().GetAsync(default, default, default, TestContext.Current.CancellationToken);
        await _authz.DidNotReceive().GetAccessibleResourceIdsAsync(
            Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Arg.Any<Permission>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheUsage_IsAskedForTheCallersReadableLibrariesVaultsAndPipelines_Only()
    {
        Readable(ResourceType.VariableLibrary, [3]);
        Readable(ResourceType.Vault, []);
        Readable(ResourceType.Pipeline, [11, 12]);

        await _sut.Get(TestContext.Current.CancellationToken);

        await _usage.Received(1).GetAsync(
            Arg.Is<List<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 3 })),
            Arg.Is<List<int>?>(ids => ids != null && ids.Count == 0),
            Arg.Is<List<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 11, 12 })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAdministrator_WhoReadsEverything_IsAnsweredForEverything()
    {
        // A null list is "every resource" (an administrator): it must never be taken for "none".
        Readable(ResourceType.VariableLibrary, null);
        Readable(ResourceType.Vault, null);
        Readable(ResourceType.Pipeline, null);

        await _sut.Get(TestContext.Current.CancellationToken);

        await _usage.Received(1).GetAsync(null, null, null, Arg.Any<CancellationToken>());
    }
}
