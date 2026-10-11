// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// D9 / SITE-FUNC-TEMPLATE-USE-001: "Use" sends only the templates the setup wizard can preselect to
/// the wizard; every other template opens the editor prefilled from it.
/// </summary>
public sealed class PipelineTemplateUseRouteTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineTemplateUseRouteTests() => _handler = BunitTestHelper.RegisterServices(this);

    public static TheoryData<string> RoleTemplateNames() => new(PipelineTemplateUseRoute.WizardRoleTemplates);

    [Theory]
    [MemberData(nameof(RoleTemplateNames))]
    public async Task EveryTemplateRoutedToTheWizard_IsOneTheWizardPreselects(string name)
    {
        Assert.Equal("/pipelines/setup?templateId=3", PipelineTemplateUseRoute.For(3, name));
        Assert.True(await WizardCanLeaveTheRoleStepAsync(name));
    }

    [Fact]
    public async Task CustomTemplate_OpensThePrefilledEditor_BecauseTheWizardWouldSelectNoRole()
    {
        Assert.Equal("/pipelines/new?templateId=3", PipelineTemplateUseRoute.For(3, "QA"));
        Assert.False(await WizardCanLeaveTheRoleStepAsync("QA"));
    }

    /// <summary>Opens the wizard on the template and reports whether its role step (which needs at
    /// least one selected role) lets the user go on.</summary>
    private async Task<bool> WizardCanLeaveTheRoleStepAsync(string name)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas" }]);
        _handler.SetJsonResponse("api/pipelines/templates",
            new List<PipelineTemplateSummaryDto> { new() { Id = 3, Name = name, Version = 1 } });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/pipelines/setup?projectId=7&templateId=3");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        // A package or external template chooses its type; an application template leaves it to be asked.
        if (cut.FindAll(".omni-selectable-card--selected").Count == 0)
        {
            await cut.Find("[data-testid='setup-architecture-application']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
            await cut.Find("[data-testid='setup-same-project'] input[value='False']").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "False" });
        }
        await cut.InvokeAsync(wizard.Instance.NextStep);
        Assert.Equal(2, wizard.Instance.CurrentStep);
        return wizard.Instance.CanAdvance;
    }
}
