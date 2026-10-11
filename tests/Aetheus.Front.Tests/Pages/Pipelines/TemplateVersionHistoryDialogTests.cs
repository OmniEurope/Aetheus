// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class TemplateVersionHistoryDialogTests : BunitContext
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public TemplateVersionHistoryDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/pipelines/templates/5/versions/2",
            new PipelineTemplateVersionDto { Version = 2, YamlContent = "name: two" });
        _handler.SetJsonResponse("api/pipelines/templates/5/versions/3",
            new PipelineTemplateVersionDto { Version = 3, YamlContent = "name: three" });
    }

    [Fact]
    public async Task LoadsMetadataPageAndOnlySelectedYamlVersions()
    {
        _handler.SetJsonResponse("api/pipelines/templates/5/versions",
            new PaginatedResult<PipelineTemplateVersionSummaryDto>
            {
                Items =
                [
                    new PipelineTemplateVersionSummaryDto
                    {
                        Version = 12,
                        ChangelogEntry = "page-two",
                        CreatedByUsername = "alice"
                    }
                ],
                TotalCount = 21,
                Page = 2,
                PageSize = 10
            });
        var cut = RenderDialog();

        await InvokeAsync(cut, "LoadVersionsAsync",
            new GridLoadArgs { Skip = 10, Top = 10, OrderBy = "Version desc" });
        cut.Render();

        Assert.Contains("page-two", cut.Markup);
        // SITE-FUNC-TEMPLATE-001: `v@version.Version` was parsed as an e-mail address.
        Assert.Contains(">v12<", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("v@version", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("page=2", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, request => request.Url.EndsWith("/versions/2", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, request => request.Url.EndsWith("/versions/3", StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.Requests, request => request.Url.EndsWith("/versions/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangingSelectionLoadsRequestedYamlOnDemand()
    {
        _handler.SetJsonResponse("api/pipelines/templates/5/versions",
            new PaginatedResult<PipelineTemplateVersionSummaryDto>());
        _handler.SetJsonResponse("api/pipelines/templates/5/versions/1",
            new PipelineTemplateVersionDto { Version = 1, YamlContent = "name: one" });
        var cut = RenderDialog();

        await InvokeAsync(cut, "OnOriginalVersionChanged", 1);

        Assert.Contains(_handler.Requests, request => request.Url.EndsWith("/versions/1", StringComparison.Ordinal));
        var originalYaml = (string)typeof(TemplateVersionHistoryDialog)
            .GetField("_originalYaml", PrivateInstance)!.GetValue(cut.Instance)!;
        Assert.Equal("name: one", originalYaml);
    }

    private IRenderedComponent<TemplateVersionHistoryDialog> RenderDialog() =>
        Render<TemplateVersionHistoryDialog>(parameters => parameters
            .Add(component => component.TemplateId, 5)
            .Add(component => component.TemplateName, "Build")
            .Add(component => component.LatestVersion, 3));

    private static async Task InvokeAsync(
        IRenderedComponent<TemplateVersionHistoryDialog> cut, string methodName, object argument)
    {
        var method = typeof(TemplateVersionHistoryDialog).GetMethod(methodName, PrivateInstance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [argument])!);
    }
}
