// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>R-56: a ?key= link to a secret that does not exist yet opens the add form with the key filled.</summary>
public sealed class VaultEditKeyLinkTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public VaultEditKeyLinkTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
    }

    private IRenderedComponent<VaultEdit> RenderLinked(string key, params string[] existingKeys)
    {
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "Production Vault",
            ProjectId = 1,
            RowVersion = Guid.NewGuid(),
            Secrets = [.. existingKeys.Select((existing, index) => new VaultSecretDto { Id = index + 1, Key = existing, CreatedAt = DateTime.UtcNow })]
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo($"http://test/vaults/1?key={key}");
        return Render<VaultEdit>(p => p.Add(x => x.Id, 1));
    }

    [Fact]
    public void AMissingKey_OpensTheAddFormWithTheKeyFilled_AndTheMaskedValueFocused()
    {
        var cut = RenderLinked("DB_PASSWORD");

        cut.WaitForAssertion(() => Assert.Equal("DB_PASSWORD", cut.Find("input[aria-label='Key']").GetAttribute("value")));
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations,
            invocation => invocation.Identifier == "Aetheus.focusById" && invocation.Arguments[0] as string == "vault-new-secret-value"));
        // The value field is still the masked one, and empty.
        Assert.NotEmpty(cut.FindAll("#vault-new-secret-value"));
        Assert.Equal(string.Empty, cut.Find("#vault-new-secret-value").GetAttribute("value") ?? string.Empty);
        cut.WaitForAssertion(() => Assert.DoesNotContain("key=", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
    }

    /// <summary>R-24: a vault created from a missing key's link opens on that key, to ask for the value right after.</summary>
    [Fact]
    public async Task ACreationLinkedFromAMissingKey_OpensTheNewVaultOnThatKey()
    {
        _handler.SetJsonResponse("api/vaults", new VaultDetailDto { Id = 99, Name = "w1.prod" });
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://test/vaults/new?projectId=1&key=DB_PASSWORD&returnUrl=%2Fprojects%2F1%2Foverview");
        var cut = Render<VaultEdit>();
        cut.Find("#vault-name").Input("w1.prod");
        await cut.Find("form").SubmitAsync();

        cut.WaitForAssertion(() => Assert.Equal(
            $"http://test/vaults/99?key=DB_PASSWORD&returnUrl={Uri.EscapeDataString("/projects/1/overview")}", nav.Uri));
    }

    [Fact]
    public void AnExistingKey_KeepsTheFilter_AndTheAddFormStaysEmpty()
    {
        var cut = RenderLinked("DB_PASSWORD", "DB_PASSWORD");

        cut.WaitForAssertion(() => Assert.Contains("DB_PASSWORD", cut.Markup, StringComparison.Ordinal));

        Assert.Equal(string.Empty, cut.Find("input[aria-label='Key']").GetAttribute("value") ?? string.Empty);
        Assert.Contains("key=DB_PASSWORD", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.focusById");
    }
}
