// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-24: a key a vault or library of the project holds, that the pipeline does not declare, has a one-click action
/// "declare it in the pipeline": confirmed first, written through the pipeline route, the host reads its readiness again;
/// a refusal says its reason; whoever may not write the pipeline keeps the link that opens it.</summary>
public sealed class PipelineReadinessDeclareHolderTests : BunitContext
{
    private const string Action = "[data-testid='setup-readiness-undefined-declare-action']";
    private const string Url = "api/pipelines/176/declared-holders";

    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;
    private int _changed;

    public PipelineReadinessDeclareHolderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private static PipelineSetupReadinessUndefinedVariableDto Row(
        PipelineSetupReadinessContainerKind kind = PipelineSetupReadinessContainerKind.Vault) => new()
        {
            Name = "JWT_KEY",
            Container = PipelineSetupReadinessContainerKind.Vault,
            ExistingHolderId = 9,
            ExistingHolderName = "w1.prod",
            ExistingHolderKind = kind,
            PipelineId = 176,
            PipelineName = "w1-release-fast"
        };

    private IRenderedComponent<PipelineReadinessChecks> Render(string returnUrl = "/projects/4/overview", PipelineSetupReadinessUndefinedVariableDto? row = null) =>
        Render<PipelineReadinessChecks>(parameters => parameters
            .Add(component => component.ProjectId, 4)
            .Add(component => component.ReturnUrl, returnUrl)
            .Add(component => component.OnChanged, EventCallback.Factory.Create(this, () => _changed++))
            .Add(component => component.Checks, new[]
            {
                new PipelineSetupReadinessCheckDto
                {
                    Kind = PipelineSetupReadinessKind.UndefinedVariables,
                    Severity = PipelineSetupReadinessSeverity.Blocking,
                    Items = ["JWT_KEY"],
                    UndefinedVariables = [row ?? Row()]
                }
            }));

    private static Task ClickMainAsync(IRenderedComponent<PipelineReadinessChecks> cut) =>
        cut.Find($"{Action} .omni-split-button__main").ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task OneClick_ConfirmsThenWrites_ThenTheHostReadsAgain_WithASuccessToast()
    {
        _dialog.ConfirmResult = true;
        _handler.SetJsonResponse(HttpMethod.Post, Url, new PipelineDto { Id = 176, Name = "w1-release-fast" });
        var cut = Render();

        Assert.Equal("ReadinessDeclareHolder", cut.Find($"{Action} .omni-split-button__main").TextContent.Trim());
        await ClickMainAsync(cut);

        Assert.Equal("ReadinessDeclareVaultConfirm", _dialog.LastConfirmMessage);
        Assert.Equal("ReadinessDeclareTitle", _dialog.LastTitle);
        var request = Assert.Single(_handler.RequestDetails, call => call.Method == "POST" && call.Url.Contains(Url, StringComparison.Ordinal));
        Assert.Contains("\"name\":\"w1.prod\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":1", request.Body, StringComparison.Ordinal);
        Assert.Equal(1, _changed);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task ALibrary_IsDeclaredAsALibrary_WithItsOwnConfirmation()
    {
        _dialog.ConfirmResult = true;
        _handler.SetJsonResponse(HttpMethod.Post, Url, new PipelineDto { Id = 176, Name = "w1-release-fast" });
        var cut = Render(row: Row(PipelineSetupReadinessContainerKind.Library));

        await ClickMainAsync(cut);

        Assert.Equal("ReadinessDeclareLibraryConfirm", _dialog.LastConfirmMessage);
        var request = Assert.Single(_handler.RequestDetails, call => call.Method == "POST" && call.Url.Contains(Url, StringComparison.Ordinal));
        Assert.Contains("\"kind\":0", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecliningTheConfirmation_WritesNothing_AndTheHostIsNotAsked()
    {
        _dialog.ConfirmResult = false;
        var cut = Render();

        await ClickMainAsync(cut);

        Assert.DoesNotContain(_handler.RequestDetails, call => call.Method == "POST" && call.Url.Contains(Url, StringComparison.Ordinal));
        Assert.Equal(0, _changed);
        Assert.Empty(Services.Toasts());
    }

    [Fact]
    public async Task ARefusal_ShowsItsReason_AndTheHostIsNotAsked()
    {
        _dialog.ConfirmResult = true;
        _handler.SetRawResponse(HttpMethod.Post, Url, "{\"message\":\"stage 'x' has no steps\"}", "application/json", HttpStatusCode.BadRequest);
        var cut = Render();

        await ClickMainAsync(cut);

        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Danger, toast.Severity);
        Assert.Contains("stage 'x' has no steps", toast.Detail, StringComparison.Ordinal);
        Assert.Equal(0, _changed);
    }

    [Fact]
    public async Task ARepositoryChangeNotSeen_409_ShowsItsReason()
    {
        _dialog.ConfirmResult = true;
        _handler.SetRawResponse(HttpMethod.Post, Url, "{\"message\":\"The repository changed\"}", "application/json", HttpStatusCode.Conflict);
        var cut = Render();

        await ClickMainAsync(cut);

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail.Contains("The repository changed", StringComparison.Ordinal));
        Assert.Equal(0, _changed);
    }

    [Fact]
    public async Task AMissingRight_403_IsSaidInItsOwnSentence()
    {
        _dialog.ConfirmResult = true;
        _handler.SetRawResponse(HttpMethod.Post, Url, string.Empty, "application/json", HttpStatusCode.Forbidden);
        var cut = Render();

        await ClickMainAsync(cut);

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "ReadinessDeclareForbidden");
        Assert.Equal(0, _changed);
    }

    [Fact]
    public void WhoMayNotWriteThePipeline_KeepsTheLinkThatOpensIt_AndNoAction()
    {
        Services.GetRequiredService<Aetheus.Front.Components.Shared.PermissionService>().SetPermissions([], isAdmin: false);

        var cut = Render();

        Assert.Empty(cut.FindAll(Action));
        Assert.Equal("/pipelines/176?returnUrl=%2Fprojects%2F4%2Foverview",
            cut.Find("[data-testid='setup-readiness-undefined-declare']").GetAttribute("href"));
    }

    [Fact]
    public void OnThePipelinesOwnPage_TheActionIsAPlainButton_WithoutAMenuToOpenTheSamePage()
    {
        var cut = Render("/pipelines/176");

        Assert.Empty(cut.FindAll($"{Action} .omni-split-button__toggle"));
        Assert.Equal("ReadinessDeclareHolder", cut.Find(Action).TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-undefined-declare']"));
    }

    [Fact]
    public async Task OpeningThePipelineByHand_IsTheSecondaryEntryOfTheMenu()
    {
        var cut = Render();

        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-undefined-declare']"));
        await cut.Find($"{Action} .omni-split-button__toggle").ClickAsync(new MouseEventArgs());

        var entry = cut.WaitForElement("[data-testid='setup-readiness-undefined-declare']");
        Assert.Equal("/pipelines/176?returnUrl=%2Fprojects%2F4%2Foverview", entry.GetAttribute("href"));
        Assert.Contains("ReadinessDeclareUnderVaults", entry.TextContent, StringComparison.Ordinal);
    }
}
