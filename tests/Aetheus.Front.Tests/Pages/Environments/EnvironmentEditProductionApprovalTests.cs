// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Environments;
using Bunit;

namespace Aetheus.Front.Tests.Pages.EnvironmentsTests;

/// <summary>R-70: "Approval required" is checked by default when the environment type is Production, on the
/// creation page; the user can uncheck it, and an environment stored without it is not changed on load.</summary>
public sealed class EnvironmentEditProductionApprovalTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public EnvironmentEditProductionApprovalTests() => _handler = BunitTestHelper.RegisterServices(this);

    private void Stubs()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [new ProjectDto { Id = 1, Name = "P1" }], TotalCount = 1 });
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>());
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>());
    }

    private static bool RequireApproval(IRenderedComponent<EnvironmentEdit> cut)
    {
        var model = typeof(EnvironmentEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        return (bool)model.GetType().GetProperty("RequireApproval")!.GetValue(model)!;
    }

    private static async Task ChooseTypeAsync(IRenderedComponent<EnvironmentEdit> cut, EnvironmentType type)
    {
        var dropDown = cut.FindComponent<OmniDropDown<EnvironmentType>>();
        await cut.InvokeAsync(() => dropDown.Instance.ValueChanged.InvokeAsync(type));
    }

    [Fact]
    public async Task ANewEnvironment_StartsWithoutApproval_AndChoosingProductionChecksIt()
    {
        Stubs();
        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.FindAll("input").Count > 0);
        Assert.False(RequireApproval(cut));

        await ChooseTypeAsync(cut, EnvironmentType.Production);

        Assert.True(RequireApproval(cut));
    }

    [Fact]
    public async Task TheUserCanUncheckTheApprovalAfterwards()
    {
        Stubs();
        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.FindAll("input").Count > 0);
        await ChooseTypeAsync(cut, EnvironmentType.Production);
        var model = typeof(EnvironmentEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;

        model.GetType().GetProperty("RequireApproval")!.SetValue(model, false);

        Assert.False(RequireApproval(cut));
    }

    [Fact]
    public async Task AnotherType_LeavesTheApprovalAlone()
    {
        Stubs();
        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.FindAll("input").Count > 0);

        await ChooseTypeAsync(cut, EnvironmentType.Staging);

        Assert.False(RequireApproval(cut));
    }

    [Fact]
    public void AStoredProductionEnvironmentWithoutApproval_IsLoadedAsStored()
    {
        Stubs();
        _handler.SetJsonResponse("api/environments/3", new EnvironmentDto
        {
            Id = 3,
            Name = "prod",
            Type = EnvironmentType.Production,
            RequireApproval = false,
            Servers = []
        });

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 3));
        cut.WaitForState(() => cut.Markup.Contains("prod", StringComparison.Ordinal));

        Assert.False(RequireApproval(cut));
    }
}
