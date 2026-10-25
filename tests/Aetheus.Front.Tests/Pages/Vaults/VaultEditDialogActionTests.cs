// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aetheus.Front.Components.Vaults;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Vaults;

/// <summary>Exercises vault and secret actions with an immediate dialog double, including
/// confirmation and cancellation paths.</summary>
public sealed class VaultEditDialogActionTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public VaultEditDialogActionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "aetheus" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "prod-vault",
            Description = "production secrets",
            ProjectId = 1,
            RowVersion = Guid.NewGuid(),
            Secrets =
            [
                new VaultSecretDto { Id = 10, Key = "DB_PASS", ExpiresAt = Now.AddDays(30), VersionCount = 2 }
            ]
        });
    }

    private IRenderedComponent<VaultEdit> RenderVault() =>
        Render<VaultEdit>(parameters => parameters.Add(page => page.Id, 1));

    // R1-4: Edit is the row's visible action, the others are entries of its "More actions" menu.
    // The vault's own tools menu comes first and also has a Delete; the secret row's menu is the last trigger.
    private static IElement Button(IRenderedComponent<VaultEdit> cut, string title)
    {
        if (cut.FindAll("button").FirstOrDefault(button =>
                string.Equals(button.GetAttribute("title"), title, StringComparison.Ordinal)) is { } button)
            return button;
        cut.FindAll(".omni-overflow-menu__trigger").Last().Click();
        return cut.FindAll(".omni-menu__item")
            .First(entry => entry.Names().Contains(title, StringComparison.Ordinal));
    }

    private static IElement TextButton(IRenderedComponent<VaultEdit> cut, string text) =>
        cut.FindAll("button").First(button => button.TextContent.Contains(text, StringComparison.Ordinal));

    /// <summary>Import and Export moved into the three-dots tools menu, which renders its items only
    /// while open. Opening it first is what a user does, so the test does the same.</summary>
    private static IElement ToolsMenuItem(IRenderedComponent<VaultEdit> cut, string text)
    {
        cut.Find(".omni-overflow-menu__trigger").Click();
        return cut.FindAll(".omni-menu__item")
            .First(item => item.TextContent.Contains(text, StringComparison.Ordinal));
    }

    private bool Sent(string method, string urlContains) =>
        _handler.Requests.Any(request => request.Method == method
            && request.Url.Contains(urlContains, StringComparison.Ordinal));

    private T LastBody<T>(string method, string urlContains)
    {
        var body = _handler.RequestDetails
            .Last(request => request.Method == method
                && request.Url.Contains(urlContains, StringComparison.Ordinal))
            .Body;
        return JsonSerializer.Deserialize<T>(body!, Json)!;
    }

    // ---------- deleting the vault ----------

    [Fact]
    public void DeletingTheVaultIsGatedByAConfirmation()
    {
        _dialog.ConfirmResult = false;
        var cut = RenderVault();

        ToolsMenuItem(cut, "Delete").Click(); // The vault's Delete is in the tools menu.

        Assert.False(Sent("DELETE", "api/vaults/1"));
    }

    [Fact]
    public void ConfirmingTheVaultDeleteCallsTheEndpoint()
    {
        _handler.SetResponse(HttpMethod.Delete, "api/vaults/1", HttpStatusCode.NoContent);
        _dialog.ConfirmResult = true;
        var cut = RenderVault();

        ToolsMenuItem(cut, "Delete").Click(); // The vault's Delete is in the tools menu.

        cut.WaitForAssertion(() => Assert.True(Sent("DELETE", "api/vaults/1")));
    }

    [Fact]
    public void ARefusedVaultDeleteDoesNotNavigateAway()
    {
        _handler.SetResponse(HttpMethod.Delete, "api/vaults/1", HttpStatusCode.Conflict);
        _dialog.ConfirmResult = true;
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = RenderVault();

        ToolsMenuItem(cut, "Delete").Click(); // The vault's Delete is in the tools menu.

        cut.WaitForAssertion(() => Assert.True(Sent("DELETE", "api/vaults/1")));
        Assert.DoesNotContain("/vaults", new Uri(navigation.Uri).AbsolutePath.TrimEnd('/') + "/end");
    }

    // ---------- updating a secret ----------

    /// <summary>R1-4: a secret row keeps one visible action (Edit); the others are in its menu.</summary>
    [Fact]
    public void ASecretRowShowsEditAsItsOnlyAction_AndKeepsTheOthersInTheMenu()
    {
        var cut = RenderVault();

        Assert.Single(cut.FindAll("button[title='Edit']"));
        Assert.Empty(cut.FindAll("button[title='Rotate'], button[title='Delete'], button[title='History'], button[title='CopyKeyReference']"));
        cut.FindAll(".omni-overflow-menu__trigger").Last().Click();
        var entries = cut.FindAll(".omni-menu__item").Select(entry => entry.TextContent.Trim()).ToList();
        foreach (var label in new[] { "CopyKeyReference", "History", "Rotate", "Delete" })
            Assert.Contains(label, entries);
    }

    [Fact]
    public void UpdatingASecretSendsTheValueTheDialogReturned()
    {
        _handler.SetJsonResponse(
            HttpMethod.Put, "api/vaults/1/secrets/10", new VaultSecretDto { Id = 10, Key = "DB_PASS" });
        _dialog.OpenResult = "new-secret-value";
        var cut = RenderVault();

        Button(cut, "Edit").Click();

        cut.WaitForAssertion(
            () => Assert.True(Sent("PUT", "api/vaults/1/secrets/10")));
        var request = LastBody<UpdateVaultSecretRequest>("PUT", "api/vaults/1/secrets/10");
        Assert.Equal("DB_PASS", request.Key);
        Assert.Equal("new-secret-value", request.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CancellingOrEmptyingTheSecretDialogSendsNothing(string? dialogResult)
    {
        _dialog.OpenResult = dialogResult;
        var cut = RenderVault();

        Button(cut, "Edit").Click();

        Assert.False(Sent("PUT", "api/vaults/1/secrets/10"));
    }

    // ---------- rotating a secret ----------

    [Fact]
    public void RotatingASecretNeedsBothTheConfirmationAndAValue()
    {
        _dialog.ConfirmResult = false;
        _dialog.OpenResult = "rotated-value";
        var cut = RenderVault();

        Button(cut, "Rotate").Click();

        Assert.False(Sent("POST", "api/vaults/1/secrets/10/rotate"));
    }

    [Fact]
    public void ConfirmingARotationPostsTheNewValueAndKeepsTheExpiry()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/vaults/1/secrets/10/rotate", new VaultSecretDto { Id = 10, Key = "DB_PASS" });
        _dialog.ConfirmResult = true;
        _dialog.OpenResult = "rotated-value";
        var cut = RenderVault();

        Button(cut, "Rotate").Click();

        cut.WaitForAssertion(
            () => Assert.True(Sent("POST", "api/vaults/1/secrets/10/rotate")));
        var request = LastBody<RotateVaultSecretRequest>("POST", "api/vaults/1/secrets/10/rotate");
        Assert.Equal("rotated-value", request.Value);
        // The front deserializes dates as browser-local, so compare the instant, not the Kind.
        Assert.Equal(Now.AddDays(30), request.ExpiresAt!.Value.ToUniversalTime());
    }

    [Fact]
    public void AConfirmedRotationWithNoValueSendsNothing()
    {
        _dialog.ConfirmResult = true;
        _dialog.OpenResult = null;
        var cut = RenderVault();

        Button(cut, "Rotate").Click();

        Assert.False(Sent("POST", "api/vaults/1/secrets/10/rotate"));
    }

    // ---------- deleting a secret ----------

    [Fact]
    public void DeletingASecretIsGatedByAConfirmation()
    {
        _dialog.ConfirmResult = false;
        var cut = RenderVault();

        Button(cut, "Delete").Click();

        Assert.False(Sent("DELETE", "api/vaults/1/secrets/10"));
    }

    [Fact]
    public void ConfirmingTheSecretDeleteCallsTheEndpoint()
    {
        _handler.SetResponse(HttpMethod.Delete, "api/vaults/1/secrets/10", HttpStatusCode.NoContent);
        _dialog.ConfirmResult = true;
        var cut = RenderVault();

        Button(cut, "Delete").Click();

        cut.WaitForAssertion(
            () => Assert.True(Sent("DELETE", "api/vaults/1/secrets/10")));
    }

    // ---------- FRONT-025: a refused mutation never reports success ----------

    private void AssertFailureReportedWithoutSuccess(string detail)
    {
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == detail);
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void ARefusedSecretUpdateReportsTheFailure(HttpStatusCode status)
    {
        _handler.SetResponse(HttpMethod.Put, "api/vaults/1/secrets/10", status);
        _dialog.OpenResult = "new-secret-value";
        var cut = RenderVault();

        Button(cut, "Edit").Click();

        cut.WaitForAssertion(() => AssertFailureReportedWithoutSuccess("SaveFailed"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void ARefusedRotationReportsTheFailure(HttpStatusCode status)
    {
        _handler.SetResponse(HttpMethod.Post, "api/vaults/1/secrets/10/rotate", status);
        _dialog.ConfirmResult = true;
        _dialog.OpenResult = "rotated-value";
        var cut = RenderVault();

        Button(cut, "Rotate").Click();

        cut.WaitForAssertion(() => AssertFailureReportedWithoutSuccess("SaveFailed"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied")]
    [InlineData(HttpStatusCode.InternalServerError, "DeleteFailed")]
    public void ARefusedSecretDeleteReportsTheFailure(HttpStatusCode status, string detail)
    {
        _handler.SetResponse(HttpMethod.Delete, "api/vaults/1/secrets/10", status);
        _dialog.ConfirmResult = true;
        var cut = RenderVault();

        Button(cut, "Delete").Click();

        cut.WaitForAssertion(() => AssertFailureReportedWithoutSuccess(detail));
    }

    // ---------- version history ----------

    [Fact]
    public void ShowingTheHistoryLoadsTheVersionsAndOpensTheDialogWithThem()
    {
        _handler.SetJsonResponse("api/vaults/1/secrets/10/versions", new List<VaultSecretVersionDto>
        {
            new() { Version = 2, Key = "DB_PASS", ChangedAt = Now },
            new() { Version = 1, Key = "DB_PASS", ChangedAt = Now.AddDays(-1) }
        });
        var cut = RenderVault();

        Button(cut, "History").Click();

        cut.WaitForAssertion(
            () => Assert.True(Sent("GET", "api/vaults/1/secrets/10/versions")));
        var versions = Assert.IsType<List<VaultSecretVersionDto>>(
            Assert.Contains("Versions", _dialog.LastParameters!));
        Assert.Equal([2, 1], versions.Select(version => version.Version));
    }

    // ---------- importing ----------

    [Fact]
    public void ImportingSendsTheParsedSecretsOfTheJsonPayload()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/vaults/1/import", new ImportResultDto { ImportedCount = 2 });
        _dialog.OpenResult = "[{\"key\":\"A\",\"value\":\"1\"},{\"key\":\"B\",\"value\":\"2\"}]";
        var cut = RenderVault();

        ToolsMenuItem(cut, "Import").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/vaults/1/import")));
        var request = LastBody<List<CreateVaultSecretRequest>>("POST", "api/vaults/1/import");
        Assert.Equal(["A", "B"], request.Select(secret => secret.Key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    public void ImportingRefusesAnAbsentBlankMalformedOrEmptyPayload(string? payload)
    {
        _dialog.OpenResult = payload;
        var cut = RenderVault();

        ToolsMenuItem(cut, "Import").Click();

        Assert.False(Sent("POST", "api/vaults/1/import"));
    }
}
