// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelineTemplatesControllerTests
{
    private readonly IPipelineTemplateService _templateService = Substitute.For<IPipelineTemplateService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly PipelineTemplatesController _sut;

    public PipelineTemplatesControllerTests()
    {
        _authz.HasPermissionAsync(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<Aetheus.Shared.Components.Auth.ResourceType>(),
                Arg.Any<int?>(),
                Arg.Any<Aetheus.Shared.Components.Auth.Permission>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _authz.GetAccessibleResourceIdsAsync(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Aetheus.Shared.Components.Auth.ResourceType.PipelineTemplate,
                Aetheus.Shared.Components.Auth.Permission.Read,
                Arg.Any<CancellationToken>())
            .Returns((List<int>?)null);
        _authz.GetOrganizationIdsWithPermissionAsync(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Aetheus.Shared.Components.Auth.ResourceType.PipelineTemplate, Aetheus.Shared.Components.Auth.Permission.Write, Arg.Any<CancellationToken>())
            .Returns([1]);
        _sut = new PipelineTemplatesController(_templateService, _authz);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(authenticationType: "test"))
            }
        };
    }

    [Fact]
    public async Task GetTemplates_ReturnsOk()
    {
        _templateService.GetTemplatesAsync(TestContext.Current.CancellationToken).Returns([]);

        var response = await _sut.GetTemplates(TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task GetTemplate_Found_ReturnsOk()
    {
        var template = new PipelineTemplateDto { Id = 1, Name = "T1", YamlContent = "yaml" };
        _templateService.GetTemplateAsync(1, TestContext.Current.CancellationToken).Returns(template);

        var response = await _sut.GetTemplate(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal("T1", ((PipelineTemplateDto)ok.Value!).Name);
    }

    [Fact]
    public async Task GetTemplate_NotFound_Returns404()
    {
        _templateService.GetTemplateAsync(99, TestContext.Current.CancellationToken)
            .Returns((PipelineTemplateDto?)null);

        var response = await _sut.GetTemplate(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task CreateTemplate_ReturnsCreated()
    {
        var template = new PipelineTemplateDto { Id = 1, Name = "New" };
        _templateService.CreateTemplateAsync(Arg.Any<CreatePipelineTemplateRequest>(), TestContext.Current.CancellationToken)
            .Returns(template);

        var response = await _sut.CreateTemplate(new CreatePipelineTemplateRequest { Name = "New", YamlContent = "y" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(response.Result);
    }

    [Fact]
    public async Task UpdateTemplate_Found_ReturnsOk()
    {
        var template = new PipelineTemplateDto { Id = 1, Name = "Updated" };
        _templateService.UpdateTemplateAsync(1, Arg.Any<UpdatePipelineTemplateRequest>(), TestContext.Current.CancellationToken)
            .Returns(template);

        var response = await _sut.UpdateTemplate(1, new UpdatePipelineTemplateRequest { Name = "Updated" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task UpdateTemplate_NotFound_Returns404()
    {
        _templateService.UpdateTemplateAsync(99, Arg.Any<UpdatePipelineTemplateRequest>(), TestContext.Current.CancellationToken)
            .Returns((PipelineTemplateDto?)null);

        var response = await _sut.UpdateTemplate(99, new UpdatePipelineTemplateRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task DeleteTemplate_Found_ReturnsNoContent()
    {
        _templateService.DeleteTemplateAsync(1, TestContext.Current.CancellationToken).Returns(true);

        var response = await _sut.DeleteTemplate(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(response);
    }

    [Fact]
    public async Task DeleteTemplate_NotFound_Returns404()
    {
        _templateService.DeleteTemplateAsync(99, TestContext.Current.CancellationToken).Returns(false);

        var response = await _sut.DeleteTemplate(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response);
    }

    [Fact]
    public async Task ExportTemplate_Found_ReturnsFile()
    {
        var template = new PipelineTemplateDto { Id = 1, Name = "T", YamlContent = "name: test" };
        _templateService.GetTemplateAsync(1, TestContext.Current.CancellationToken).Returns(template);

        var response = await _sut.ExportTemplate(1, TestContext.Current.CancellationToken);

        var file = Assert.IsType<FileContentResult>(response);
        Assert.Equal("application/x-yaml", file.ContentType);
    }

    [Fact]
    public async Task ExportTemplate_NotFound_Returns404()
    {
        _templateService.GetTemplateAsync(99, TestContext.Current.CancellationToken)
            .Returns((PipelineTemplateDto?)null);

        var response = await _sut.ExportTemplate(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response);
    }

    [Fact]
    public async Task ResolveTemplate_Found_ReturnsOk()
    {
        var template = new PipelineTemplateDto
        {
            Id = 1,
            Name = "T",
            OrganizationId = 42,
            YamlContent = "name: {{name}}"
        };
        _templateService.GetTemplateAsync(1, TestContext.Current.CancellationToken).Returns(template);
        _templateService.ResolveTemplateAsync(
                "name: {{name}}", Arg.Any<Dictionary<string, string>?>(), TestContext.Current.CancellationToken, 42)
            .Returns("name: resolved");

        var response = await _sut.ResolveTemplate(1, new Dictionary<string, string> { ["name"] = "resolved" }, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal("name: resolved", ok.Value);
        await _templateService.Received(1).ResolveTemplateAsync(
            "name: {{name}}", Arg.Any<Dictionary<string, string>?>(), TestContext.Current.CancellationToken, 42);
    }

    [Fact]
    public async Task ResolveTemplate_TemplateNotFound_Returns404()
    {
        _templateService.GetTemplateAsync(99, TestContext.Current.CancellationToken)
            .Returns((PipelineTemplateDto?)null);

        var response = await _sut.ResolveTemplate(99, null, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task GetTemplateVersions_ReturnsPagedMetadata()
    {
        _templateService.GetTemplateVersionsAsync(
                1, Arg.Any<PaginationRequest>(), TestContext.Current.CancellationToken)
            .Returns(new PaginatedResult<PipelineTemplateVersionSummaryDto>
            {
                Items = [new PipelineTemplateVersionSummaryDto { Version = 2 }],
                TotalCount = 12,
                Page = 2,
                PageSize = 10
            });

        var response = await _sut.GetTemplateVersions(
            1, new PaginationRequest { Page = 2, PageSize = 10 }, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(12, Assert.IsType<PaginatedResult<PipelineTemplateVersionSummaryDto>>(ok.Value).TotalCount);
    }

    [Fact]
    public async Task GetTemplateVersion_ReturnsRequestedYaml()
    {
        _templateService.GetTemplateVersionAsync(1, 7, TestContext.Current.CancellationToken)
            .Returns(new PipelineTemplateVersionDto { Version = 7, YamlContent = "name: seven" });

        var response = await _sut.GetTemplateVersion(1, 7, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal("name: seven", Assert.IsType<PipelineTemplateVersionDto>(ok.Value).YamlContent);
    }

    [Fact]
    public async Task ResolveTemplate_PinnedVersion_ResolvesRequestedYaml()
    {
        var template = new PipelineTemplateDto
        {
            Id = 1,
            OrganizationId = 42,
            YamlContent = "name: latest",
            Versions =
            [
                new PipelineTemplateVersionDto { Version = 1, YamlContent = "name: pinned\nextends: base@1" }
            ]
        };
        _templateService.GetTemplateAsync(1, TestContext.Current.CancellationToken).Returns(template);
        _templateService.GetTemplateVersionAsync(1, 1, TestContext.Current.CancellationToken).Returns(
            new PipelineTemplateVersionDto
            {
                Version = 1,
                YamlContent = "name: pinned\nextends: base@1"
            });
        _templateService.ResolveTemplateAsync("name: pinned\nextends: base@1", null, TestContext.Current.CancellationToken, 42)
            .Returns("name: recursively-resolved");

        var response = await _sut.ResolveTemplate(1, null, TestContext.Current.CancellationToken, version: 1);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal("name: recursively-resolved", ok.Value);
        await _templateService.Received(1).ResolveTemplateAsync(
            "name: pinned\nextends: base@1", null, TestContext.Current.CancellationToken, 42);
    }

    [Fact]
    public async Task ResolveTemplate_MissingPinnedVersion_Returns404()
    {
        _templateService.GetTemplateAsync(1, TestContext.Current.CancellationToken).Returns(new PipelineTemplateDto
        {
            Id = 1,
            Versions = []
        });
        _templateService.GetTemplateVersionAsync(1, 9, TestContext.Current.CancellationToken)
            .Returns((PipelineTemplateVersionDto?)null);

        var response = await _sut.ResolveTemplate(1, null, TestContext.Current.CancellationToken, version: 9);

        Assert.IsType<NotFoundResult>(response.Result);
        await _templateService.DidNotReceive().ResolveTemplateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<int>());
    }

    [Fact]
    public async Task ResolveTemplate_ResolveFails_ReturnsBadRequest()
    {
        var template = new PipelineTemplateDto { Id = 1, Name = "T", YamlContent = "bad" };
        _templateService.GetTemplateAsync(1, TestContext.Current.CancellationToken).Returns(template);
        _templateService.ResolveTemplateAsync("bad", Arg.Any<Dictionary<string, string>?>(), TestContext.Current.CancellationToken, 0)
            .Returns((string?)null);

        var response = await _sut.ResolveTemplate(1, null, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    private static Microsoft.AspNetCore.Http.FormFile YamlFile(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "ci.yaml");
    }

    [Fact]
    public async Task ImportTemplate_ValidYaml_IsCreatedInTheCallersOrganization()
    {
        _templateService.ImportTemplateAsync("name: ci", Arg.Any<CancellationToken>(), 1)
            .Returns(new PipelineTemplateDto { Id = 9, Name = "ci" });

        var response = await _sut.ImportTemplate(YamlFile("name: ci"), TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(response.Result);
        Assert.Equal(9, Assert.IsType<PipelineTemplateDto>(created.Value).Id);
    }

    [Fact]
    public async Task ImportTemplate_InvalidYaml_IsABadRequest()
    {
        var response = await _sut.ImportTemplate(YamlFile("not: [yaml"), TestContext.Current.CancellationToken);

        Assert.Equal("Invalid YAML pipeline definition.",
            Assert.IsType<ApiError>(Assert.IsType<BadRequestObjectResult>(response.Result).Value).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_048_577)]
    public async Task ImportTemplate_AnEmptyOrOversizedFile_IsABadRequest(int length)
    {
        var file = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(), 0, length, "file", "ci.yaml");

        var response = await _sut.ImportTemplate(file, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        await _templateService.DidNotReceiveWithAnyArgs().ImportTemplateAsync(default!, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task ImportTemplate_WithoutWriteRight_IsForbidden()
    {
        _authz.HasPermissionAsync(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Aetheus.Shared.Components.Auth.ResourceType.PipelineTemplate,
                null,
                Aetheus.Shared.Components.Auth.Permission.Write,
                Arg.Any<CancellationToken>())
            .Returns(false);

        Assert.IsType<ForbidResult>((await _sut.ImportTemplate(YamlFile("name: ci"), TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task ImportTemplate_ForAUserWithoutOrganization_IsForbidden()
    {
        _authz.GetOrganizationIdsWithPermissionAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Aetheus.Shared.Components.Auth.ResourceType.PipelineTemplate, Aetheus.Shared.Components.Auth.Permission.Write, Arg.Any<CancellationToken>())
            .Returns([]);

        Assert.IsType<ForbidResult>((await _sut.ImportTemplate(YamlFile("name: ci"), TestContext.Current.CancellationToken)).Result);
    }
}
