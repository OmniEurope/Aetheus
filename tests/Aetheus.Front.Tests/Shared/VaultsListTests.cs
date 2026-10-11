// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

// 0-a: the shared VaultsList drives all 3 scopes - global/project are server-paginated via
// GetVaultsAsync (projectId filter), server detail is self-loaded via GetServerVaultsAsync.
public class VaultsListTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type ListType = typeof(VaultsList);

    public VaultsListTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void ServerScope_RendersVaultNames()
    {
        _handler.SetJsonResponse("api/servers/1/vaults", new List<VaultDto>
        {
            new() { Id = 1, Name = "Prod Secrets", SecretCount = 3 }
        });

        var cut = Render<VaultsList>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForState(() => cut.Markup.Contains("Prod Secrets"), TimeSpan.FromSeconds(2));

        Assert.Contains("Prod Secrets", cut.Markup);
    }

    [Fact]
    public void ServerScope_Empty_LoadsEmptyList()
    {
        _handler.SetJsonResponse("api/servers/2/vaults", new List<VaultDto>());

        var cut = Render<VaultsList>(p => p.Add(x => x.ServerId, 2));
        cut.WaitForState(() =>
            ((List<VaultDto>?)ListType.GetField("_serverAll", Priv)!.GetValue(cut.Instance)) is not null,
            TimeSpan.FromSeconds(2));

        var all = (List<VaultDto>)ListType.GetField("_serverAll", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(all);
    }

    [Fact]
    public void GlobalScope_RendersVaults()
    {
        _handler.SetJsonResponse("api/vaults", new PaginatedResult<VaultDto>
        {
            Items = [new VaultDto { Id = 1, Name = "Globals", Description = "Global secrets", SecretCount = 2 }],
            TotalCount = 1
        });

        var cut = Render<VaultsList>();
        cut.WaitForState(() => cut.Markup.Contains("Globals"), TimeSpan.FromSeconds(2));

        Assert.Contains("Globals", cut.Markup);
    }

    [Fact]
    public void ProjectScope_RendersVaults_WithoutProjectColumn()
    {
        _handler.SetJsonResponse("api/vaults", new PaginatedResult<VaultDto>
        {
            Items = [new VaultDto { Id = 2, ProjectId = 7, ProjectName = "Scoped", Name = "ProjVault", SecretCount = 1 }],
            TotalCount = 1
        });

        var cut = Render<VaultsList>(p => p.Add(x => x.ProjectId, 7));
        cut.WaitForState(() => cut.Markup.Contains("ProjVault"), TimeSpan.FromSeconds(2));

        Assert.Contains("ProjVault", cut.Markup);
        Assert.DoesNotContain("Scoped", cut.Markup);
    }

    [Fact]
    public async Task ProjectScope_NewVault_NavigatesWithProjectId()
    {
        _handler.SetJsonResponse("api/vaults", new PaginatedResult<VaultDto> { Items = [], TotalCount = 0 });
        var cut = Render<VaultsList>(p => p.Add(x => x.ProjectId, 99));

        var method = ListType.GetMethod("NewVault", Priv)!;
        await cut.InvokeAsync(() => method.Invoke(cut.Instance, []));

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.Contains("vaults/new", nav.Uri);
        Assert.Contains("projectId=99", nav.Uri);
    }

    [Fact]
    public async Task DisposeAsync_Completes()
    {
        _handler.SetJsonResponse("api/servers/1/vaults", new List<VaultDto>());
        var cut = Render<VaultsList>(p => p.Add(x => x.ServerId, 1));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/servers/1/vaults", new List<VaultDto>
        {
            new() { Id = 1, Name = "Prod Secrets", SecretCount = 3 }
        });

        var cut = Render<VaultsList>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)), TimeSpan.FromSeconds(3));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 1);
    }
}
