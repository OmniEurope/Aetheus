// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-59: the dialog that fills one empty variable or secret over the setup wizard writes it through the
/// library and vault APIs, masks a secret, and closes with <c>true</c> so the wizard reads its readiness again.</summary>
public sealed class PipelineEmptyValueDialogTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineEmptyValueDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(),
            sp.GetRequiredService<IJSRuntime>()));
    }

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    private IRenderedComponent<PipelineEmptyValueDialog> RenderDialog(bool isVault, int ownerId, string ownerName, string key) =>
        Render<PipelineEmptyValueDialog>(parameters => parameters
            .Add(dialog => dialog.OwnerId, ownerId)
            .Add(dialog => dialog.OwnerName, ownerName)
            .Add(dialog => dialog.EntryKey, key)
            .Add(dialog => dialog.IsVault, isVault));

    [Fact]
    public void ALibraryValue_IsUpdatedInPlace_AndTheDialogClosesFilled()
    {
        // As the real API answers: the library detail carries no entries, they are read from the entries page.
        _handler.SetPaginatedJsonResponse<VariableEntryDto>(HttpMethod.Get, "api/variable-libraries/12/entries",
            [new() { Id = 4, Key = "API_HOST_ALIAS", Value = string.Empty }, new() { Id = 5, Key = "API_HOST", Value = string.Empty }]);
        _handler.SetJsonResponse(HttpMethod.Put, "api/variable-libraries/12/entries/5",
            new VariableEntryDto { Id = 5, Key = "API_HOST", Value = "api.example.org" });
        var cut = RenderDialog(isVault: false, 12, "aetheus.prod", "API_HOST");

        Assert.Contains("PipelineFillLibraryIntro", cut.Markup, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid='setup-fill-validate']").HasAttribute("disabled"));
        cut.Find("#setup-fill-value").Input("api.example.org");
        cut.Find("[data-testid='setup-fill-validate']").Click();

        cut.WaitForAssertion(() => Assert.True(Spy().Closed));
        Assert.Equal(true, Spy().LastResult);
        var put = Assert.Single(_handler.RequestDetails, request => request.Method == "PUT");
        Assert.EndsWith("api/variable-libraries/12/entries/5", put.Url, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(put.Body!);
        Assert.Equal("API_HOST", body.RootElement.GetProperty("key").GetString());
        Assert.Equal("api.example.org", body.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public void AVaultSecret_IsTypedMasked_UpdatedInPlace_AndNeverRenderedBack()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/vaults/9", new VaultDetailDto
        {
            Id = 9,
            Name = "aetheus-deploy",
            Secrets = [new() { Id = 3, Key = "DB_PASSWORD" }]
        });
        _handler.SetJsonResponse(HttpMethod.Put, "api/vaults/9/secrets/3", new VaultSecretDto { Id = 3, Key = "DB_PASSWORD" });
        var cut = RenderDialog(isVault: true, 9, "aetheus-deploy", "DB_PASSWORD");

        Assert.Contains("PipelineFillVaultIntro", cut.Markup, StringComparison.Ordinal);
        // The masked field of the vaults page: OmniPassword, never the plain text box.
        Assert.Empty(cut.FindAll("[data-testid='setup-fill-text']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='setup-fill-secret']"));
        cut.Find("#setup-fill-value").Input("s3cret!");
        cut.Find("[data-testid='setup-fill-validate']").Click();

        cut.WaitForAssertion(() => Assert.True(Spy().Closed));
        Assert.Equal(true, Spy().LastResult);
        var put = Assert.Single(_handler.RequestDetails, request => request.Method == "PUT");
        Assert.EndsWith("api/vaults/9/secrets/3", put.Url, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(put.Body!);
        Assert.Equal("s3cret!", body.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public void ARefusedSave_KeepsTheDialogOpen_SoNothingIsReportedFilled()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/vaults/9", new VaultDetailDto
        {
            Id = 9,
            Name = "aetheus-deploy",
            Secrets = [new() { Id = 3, Key = "DB_PASSWORD" }]
        });
        _handler.SetResponse(HttpMethod.Put, "api/vaults/9/secrets/3", HttpStatusCode.InternalServerError);
        var cut = RenderDialog(isVault: true, 9, "aetheus-deploy", "DB_PASSWORD");

        cut.Find("#setup-fill-value").Input("s3cret!");
        cut.Find("[data-testid='setup-fill-validate']").Click();

        cut.WaitForAssertion(() => Assert.Single(_handler.RequestDetails, request => request.Method == "PUT"));
        Assert.False(Spy().Closed);
    }
}
