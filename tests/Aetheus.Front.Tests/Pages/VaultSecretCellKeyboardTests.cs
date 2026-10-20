// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;
using HelpCenterPage = Aetheus.Front.Components.Help.HelpCenter;
using ProjectsPage = Aetheus.Front.Components.Projects.Projects;

namespace Aetheus.Front.Tests.Pages;

/// <summary>A vault secret's key cell opens the update dialog for that secret on Enter or Space.</summary>
public sealed class VaultSecretCellKeyboardTests : BunitContext
{
    public VaultSecretCellKeyboardTests()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [new ProjectDto { Id = 1, Name = "App" }], TotalCount = 1 });
        handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "Vault",
            ProjectId = 1,
            RowVersion = Guid.NewGuid(),
            Secrets = [new VaultSecretDto { Id = 10, Key = "DB_PASS", ExpiresAt = DateTime.UtcNow.AddDays(30) }]
        });
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    [Theory]
    [InlineData("Enter", 1)]
    [InlineData(" ", 1)]
    [InlineData("a", 0)]
    public async Task TheKeyCell_OpensTheUpdateDialogOnEnterOrSpaceOnly(string key, int opened)
    {
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("DB_PASS", cut.Markup, StringComparison.Ordinal));

        await cut.FindAll(".clickable-cell").First(span => span.TextContent == "DB_PASS").KeyDownAsync(new KeyboardEventArgs { Key = key });

        Assert.Equal(opened, Dialog.OpenCount);
        if (opened == 1)
        {
            Assert.Equal(typeof(SecretValueDialog), Dialog.LastComponent);
            Assert.Equal("DB_PASS", Dialog.LastParameters!["SecretKey"]);
        }
    }
}
