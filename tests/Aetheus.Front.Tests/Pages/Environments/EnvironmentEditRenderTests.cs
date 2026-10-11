// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Environments;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.EnvironmentsTests;

public class EnvironmentEditRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public EnvironmentEditRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>());
    }

    private static EnvironmentDto BuildEnvironment() => new()
    {
        Id = 1,
        Name = "production",
        Description = "Main production environment",
        Type = EnvironmentType.Production,
        ProjectId = 1,
        ProjectName = "MyProject",
        RequireApproval = true,
        ApprovalTimeoutMinutes = 60,
        ApprovalInstructions = "Get approval from lead.",
        Servers = [new EnvironmentServerDto { ServerId = 5, ServerName = "web-01", ServerStatus = ServerStatus.Online }]
    };

    private static PaginatedResult<ProjectDto> BuildProjects() => new()
    {
        Items = [new ProjectDto { Id = 1, Name = "MyProject" }],
        TotalCount = 1
    };

    private static PaginatedResult<ServerDto> BuildServers() => new()
    {
        Items = [new ServerDto { Id = 5, Name = "web-01", Status = ServerStatus.Online, Type = ServerType.Normal }],
        TotalCount = 1
    };

    private void SetupStubs(int envId = 1)
    {
        _handler.SetJsonResponse("api/projects", BuildProjects());
        _handler.SetJsonResponse("api/servers", BuildServers());
        _handler.SetJsonResponse($"api/environments/{envId}", BuildEnvironment());
    }

    [Fact]
    public void Renders_ExistingEnvironment()
    {
        SetupStubs();

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50, TimeSpan.FromSeconds(2));

        // Edit mode fetches environment 1 and populates the detail with its name.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/environments/1"));
        var detail = (EnvironmentDto?)typeof(EnvironmentEdit)
            .GetField("_detail", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.NotNull(detail);
        Assert.Equal("production", detail!.Name);
    }

    [Fact]
    public void Renders_NewEnvironment_WithoutId()
    {
        _handler.SetJsonResponse("api/projects", BuildProjects());
        _handler.SetJsonResponse("api/servers", BuildServers());

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50, TimeSpan.FromSeconds(2));

        // New-environment mode loads projects and servers but never fetches an environment detail.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/projects"));
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/servers"));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("api/environments/"));
    }

    [Fact]
    public void IsNew_IsTrueWhenIdIsNull()
    {
        _handler.SetJsonResponse("api/projects", BuildProjects());
        _handler.SetJsonResponse("api/servers", BuildServers());

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50, TimeSpan.FromSeconds(2));

        var isNew = (bool)typeof(EnvironmentEdit)
            .GetProperty("_isNew", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.True(isNew);
    }

    [Fact]
    public void IsNew_IsFalseWhenIdIsSet()
    {
        SetupStubs();

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50, TimeSpan.FromSeconds(2));

        var isNew = (bool)typeof(EnvironmentEdit)
            .GetProperty("_isNew", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.False(isNew);
    }

    [Fact]
    public void TypeStyle_ReturnsCorrectBadgeStyles()
    {
        var method = typeof(EnvironmentEdit).GetMethod("TypeStyle", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(OmniTone.Danger, method.Invoke(null, [EnvironmentType.Production]));
        Assert.Equal(OmniTone.Warning, method.Invoke(null, [EnvironmentType.Staging]));
        Assert.Equal(OmniTone.Accent, method.Invoke(null, [EnvironmentType.Testing]));
        Assert.Equal(OmniTone.Success, method.Invoke(null, [EnvironmentType.Development]));
    }

    [Fact]
    public void Renders_EnvironmentWithApproval()
    {
        SetupStubs();

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50, TimeSpan.FromSeconds(2));

        var detail = (EnvironmentDto?)typeof(EnvironmentEdit)
            .GetField("_detail", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance);
        Assert.NotNull(detail);
        Assert.True(detail.RequireApproval);
    }

    /// <summary>
    /// Recette R2-001: advancing a branch is an explicit <c>type: advance-branch</c> step of the deploy
    /// pipeline; the environment form no longer offers the hidden switch that did it.
    /// </summary>
    [Fact]
    public void DoesNotOfferTheRetiredBranchAdvanceSetting()
    {
        SetupStubs();

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));
        // The approval instructions field only renders once the loaded environment is bound, so the
        // assertions below run against the populated form, not an empty shell.
        cut.WaitForState(() => cut.Markup.Contains("ApprovalInstructions", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));

        Assert.DoesNotContain("AdvanceBranch", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Advance a branch", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>Recette R1-24: the approval instructions field says what it is for, and the field is
    /// described by that explanation for assistive technologies.</summary>
    [Fact]
    public void R1_24_ApprovalInstructions_ExplainWhoReadsThem()
    {
        SetupStubs();

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.FindAll("textarea#ApprovalInstructions").Count == 1, TimeSpan.FromSeconds(2));

        Assert.Contains("ApprovalInstructionsHelp", cut.Find("#ApprovalInstructionsField-description").TextContent, StringComparison.Ordinal);
        Assert.Equal("ApprovalInstructionsField-description",
            cut.Find("textarea#ApprovalInstructions").GetAttribute("aria-describedby"));
    }

    [Fact]
    public void Renders_NewEnvironment_WithProjectIdQuery()
    {
        _handler.SetJsonResponse("api/projects", BuildProjects());
        _handler.SetJsonResponse("api/servers", BuildServers());

        // ProjectId is [SupplyParameterFromQuery] - bUnit requires NavigationManager navigation
        // rather than p.Add() to supply query parameters. Navigate first, then render.
        var navManager = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navManager.NavigateTo("http://localhost/?ProjectId=1");

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50, TimeSpan.FromSeconds(2));

        // The ?ProjectId=1 query matches a loaded project, so the new model pre-selects it.
        var model = typeof(EnvironmentEdit)
            .GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var projectId = (int?)model.GetType().GetProperty("ProjectId")!.GetValue(model);
        Assert.Equal(1, projectId);
    }
}
