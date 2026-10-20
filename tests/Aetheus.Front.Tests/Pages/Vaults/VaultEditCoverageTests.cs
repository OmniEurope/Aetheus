// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Vaults;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Fills remaining gaps in VaultEdit coverage: OnParametersSetAsync with EnvironmentId/
/// ProjectServerId query parameters, CopyKeyReference, and IsNew with Id=0.
/// </summary>
public class VaultEditCoverageTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;

    public VaultEditCoverageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupProjects()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "MyProject" }],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/projects/1/servers",
            new List<ProjectServerDto> { new() { Id = 3, ProjectId = 1, DisplayName = "Vault server" } });
    }

    private void SetupVault(int id = 1)
    {
        SetupProjects();
        _handler.SetJsonResponse($"api/vaults/{id}", new VaultDetailDto
        {
            Id = id,
            Name = "TestVault",
            Description = "A vault",
            ProjectId = 1,
            RowVersion = Guid.NewGuid(),
            Secrets = []
        });
    }

    // ── IsNew with Id=0 ──────────────────────────────────────────────────────

    [Fact]
    public void IsNew_TrueWhenIdIsZero()
    {
        SetupProjects();
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, (int?)0));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var isNew = (bool)typeof(VaultEdit).GetProperty("_isNew", Priv)!.GetValue(cut.Instance)!;
        Assert.True(isNew);
    }

    // Reads a nullable-int property off the private _model instance.
    private static int? ModelInt(Bunit.IRenderedComponent<VaultEdit> cut, string prop)
    {
        var model = typeof(VaultEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        return (int?)model.GetType().GetProperty(prop)!.GetValue(model);
    }

    // ── OnParametersSetAsync with EnvironmentId query ─────────────────────────

    [Fact]
    public void NewVault_WithEnvironmentIdQuery_SetsEnvironmentId()
    {
        SetupProjects();
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/environments/7",
            new EnvironmentDto { Id = 7, Name = "QA" });
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("http://localhost/?EnvironmentId=7");

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        // The EnvironmentId query param must propagate onto the bound model.
        Assert.Equal(7, ModelInt(cut, "EnvironmentId"));
        Assert.Null(ModelInt(cut, "ProjectServerId"));
    }

    // ── OnParametersSetAsync with ProjectServerId query ───────────────────────

    [Fact]
    public void NewVault_WithProjectServerIdQuery_SetsProjectServerId()
    {
        SetupProjects();
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("http://localhost/?ProjectServerId=3");

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        Assert.Equal(3, ModelInt(cut, "ProjectServerId"));
        Assert.Null(ModelInt(cut, "EnvironmentId"));
    }

    // ── OnParametersSetAsync with ProjectId not in list ───────────────────────

    [Fact]
    public void NewVault_WithNonMatchingProjectIdQuery_DoesNotSetProjectId()
    {
        SetupProjects(); // only project Id=1
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("http://localhost/?ProjectId=99"); // 99 not in project list

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        // 99 is not in the loaded project list → the guard rejects it, model stays unset.
        Assert.Null(ModelInt(cut, "ProjectId"));
    }

    // ── _previousId prevents double-load ─────────────────────────────────────

    [Fact]
    public void SameId_Twice_DoesNotDoubleLoad()
    {
        SetupVault(2);
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 2));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var requestsBefore = _handler.Requests.Count(r => r.Url.Contains("api/vaults/2"));

        cut.Render();
        var detail = (VaultDetailDto?)typeof(VaultEdit).GetField("_detail", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(detail);
        Assert.Equal("TestVault", detail!.Name);
        Assert.Equal(requestsBefore, _handler.Requests.Count(r => r.Url.Contains("api/vaults/2")));
    }

    // ── CopyKeyReference ─────────────────────────────────────────────────────

    [Fact]
    public async Task CopyKeyReference_CopiesAndNotifies()
    {
        SetupVault(1);
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(VaultEdit).GetMethod("CopyKeyReference", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["MY_KEY"])!);

        // Clipboard interop is fired and the user is notified of the successful copy.
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "navigator.clipboard.writeText");
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Success, notif.Toasts()[0].Severity);
    }

    // ── OnSubmit – null response does not navigate ─────────────────────────────

    [Fact]
    public async Task OnSubmit_Create_NullResponse_DoesNotNavigate()
    {
        SetupProjects();
        _handler.SetJsonResponse("api/vaults", (VaultDetailDto?)null);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var modelField = typeof(VaultEdit).GetField("_model", Priv)!;
        var model = modelField.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "TestName");

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var uriBefore = nav.Uri;

        var onSubmit = typeof(VaultEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)onSubmit.Invoke(cut.Instance, [])!);

        // A null create response must NOT navigate to a new vault detail - the URI stays put and no
        // /vaults/{id} entry is pushed onto the history.
        Assert.Equal(uriBefore, nav.Uri);
        Assert.DoesNotContain(nav.History, h => System.Text.RegularExpressions.Regex.IsMatch(h.Uri, @"vaults/\d+"));
    }

    // ── GetExpiryBadge boundary: exactly 0 days remaining ────────────────────

    [Fact]
    public void GetExpiryBadge_AtBoundaryDanger()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge", PrivStatic)!;
        // daysLeft <= 0 -> Danger
        var result = (OmniTone)method.Invoke(null, [DateTime.UtcNow.AddHours(-1)])!;
        Assert.Equal(OmniTone.Danger, result);
    }

    // ── ExportKeys with detail set ────────────────────────────────────────────

    [Fact]
    public async Task ExportKeys_WithDetail_CallsApi()
    {
        SetupVault(1);
        _handler.SetJsonResponse("api/vaults/1/export-keys", new List<string> { "DB_PASSWORD", "API_KEY" });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(VaultEdit).GetMethod("ExportKeys", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Export serializes the two keys, triggers a file download, and shows a success toast.
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "downloadFile");
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Success, notif.Toasts()[0].Severity);
    }
}
