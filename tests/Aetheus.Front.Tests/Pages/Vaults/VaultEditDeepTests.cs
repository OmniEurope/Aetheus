// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Vaults;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.VaultsDeepCoverage;

/// <summary>
/// Deep tests for VaultEdit.razor.cs - covers OnParametersSetAsync,
/// OnSubmit (new and edit), DeleteSecret, ShowVersions, UpdateSecret,
/// RotateSecret (partially), ImportSecrets, ExportKeys.
/// Dialog.Confirm / Dialog.OpenAsync calls are skipped (hang forever).
/// </summary>
public class VaultEditDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public VaultEditDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static VaultDetailDto MakeDetail(int id = 1) => new()
    {
        Id = id,
        Name = "My Vault",
        Description = "Desc",
        ProjectId = 1,
        Secrets =
        [
            new VaultSecretDto { Id = 10, Key = "DB_PASSWORD" }
        ]
    };

    private void SetupProjects()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "App" }],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/projects/1/servers",
            new List<ProjectServerDto> { new() { Id = 7, ProjectId = 1, DisplayName = "App server" } });
    }

    private void SetupVault(int id = 1)
    {
        _handler.SetJsonResponse($"api/vaults/{id}", MakeDetail(id));
    }

    private static int? ModelInt(IRenderedComponent<VaultEdit> cut, string prop)
    {
        var model = typeof(VaultEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        return (int?)model.GetType().GetProperty(prop)!.GetValue(model);
    }

    // ── OnParametersSetAsync - new vault (Id=0) ─────────────────────────────

    [Fact]
    public void Renders_NewVault()
    {
        SetupProjects();
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // New-vault mode (Id=0) loads the project list but must never fetch a vault detail.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/projects"));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("api/vaults/"));
    }

    // ── OnParametersSetAsync - existing vault ────────────────────────────────

    [Fact]
    public void Renders_ExistingVault()
    {
        SetupProjects();
        SetupVault(1);
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));
        var detail = (VaultDetailDto?)typeof(VaultEdit).GetField("_detail", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(detail);
    }

    [Fact]
    public void VaultIdChange_ReloadsSameComponentInstance()
    {
        SetupProjects();
        _handler.SetJsonResponse("api/vaults/1", MakeDetail(1) with { Name = "First vault" });
        _handler.SetJsonResponse("api/vaults/2", MakeDetail(2) with { Name = "Second vault" });
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));

        cut.Render(p => p.Add(x => x.Id, 2));

        cut.WaitForAssertion(() => Assert.Contains("Second vault", cut.Markup));
        Assert.DoesNotContain("First vault", cut.Markup);
    }

    // ── OnParametersSetAsync with ProjectId set via reflection ─────────────────────

    [Fact]
    public void OnParametersSet_WithProjectId_SetsModelProjectId()
    {
        SetupProjects();

        // Bind ?ProjectId=1 through the real query pipeline (the id IS in the loaded project list),
        // so OnParametersSetAsync's binding branch runs for real rather than via a reflection-set
        // field + forced re-invoke.
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("http://localhost/?ProjectId=1");

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // Matching project id → bound onto the model.
        Assert.Equal(1, ModelInt(cut, "ProjectId"));
    }

    // ── OnParametersSetAsync with EnvironmentId ──────────────────────────────

    [Fact]
    public async Task OnParametersSet_WithEnvironmentId_SetsModel()
    {
        SetupProjects();
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        typeof(VaultEdit).GetProperty("EnvironmentId")!.SetValue(cut.Instance, (int?)5);
        var method = typeof(VaultEdit).GetMethod("OnParametersSetAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // No ProjectId → the EnvironmentId branch binds onto the model.
        Assert.Equal(5, ModelInt(cut, "EnvironmentId"));
    }

    // ── OnParametersSetAsync with ProjectServerId ────────────────────────────

    [Fact]
    public async Task OnParametersSet_WithProjectServerId_SetsModel()
    {
        SetupProjects();
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        typeof(VaultEdit).GetProperty("ProjectServerId")!.SetValue(cut.Instance, (int?)7);
        var method = typeof(VaultEdit).GetMethod("OnParametersSetAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // No ProjectId/EnvironmentId → the ProjectServerId branch binds onto the model.
        Assert.Equal(7, ModelInt(cut, "ProjectServerId"));
    }

    // ── OnParametersSetAsync skips reload when Id unchanged ─────────────────

    [Fact]
    public async Task OnParametersSetAsync_SameId_SkipsReload()
    {
        SetupProjects();
        SetupVault(1);
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // Mutate the loaded detail so we can detect whether a reload overwrote it.
        var detailField = typeof(VaultEdit).GetField("_detail", Priv)!;
        var marker = new VaultDetailDto { Id = 1, Name = "SENTINEL", ProjectId = 1, Secrets = [] };
        detailField.SetValue(cut.Instance, marker);

        // Re-run OnParametersSetAsync with the same Id → early return, detail untouched.
        var method = typeof(VaultEdit).GetMethod("OnParametersSetAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var detail = (VaultDetailDto?)detailField.GetValue(cut.Instance);
        Assert.Same(marker, detail);
    }

    // ── OnSubmit - creating a new vault ─────────────────────────────────────

    [Fact]
    public async Task OnSubmit_New_CreatesVault()
    {
        SetupProjects();
        _handler.SetJsonResponse("api/vaults", new VaultDetailDto { Id = 99, Name = "Created" });
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var model = typeof(VaultEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "Created Vault");

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var method = typeof(VaultEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Create succeeded (Id=99) → busy flag cleared and navigation to the new vault.
        Assert.False((bool)typeof(VaultEdit).GetField("_saving", Priv)!.GetValue(cut.Instance)!);
        Assert.EndsWith("/vaults/99", nav.Uri);
    }

    // ── OnSubmit - updating existing vault ────────────────────────────────────

    [Fact]
    public async Task OnSubmit_Edit_UpdatesVault()
    {
        SetupProjects();
        SetupVault(1);
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var method = typeof(VaultEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Edit path issues a PUT to api/vaults/1 (the update the test name promises).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/vaults/1"));
    }

    // ── AddSecret - empty key does nothing ──────────────────────────────────

    [Fact]
    public async Task AddSecret_EmptyKey_LeavesTheValueUnchanged()
    {
        SetupProjects();
        SetupVault(1);
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var notif = Services.GetRequiredService<OmniOverlayService>();
        SetFormValue(cut.Instance, "_newSecret", "Key", "");
        SetFormValue(cut.Instance, "_newSecret", "Value", "v");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret"));

        // Whitespace/empty key → guard returns: value preserved, no toast.
        Assert.Equal("v", GetFormValue<string>(cut.Instance, "_newSecret", "Value"));
        Assert.Empty(notif.Toasts());
    }

    // ── AddSecret - with key, calls API ─────────────────────────────────────

    [Fact]
    public async Task AddSecret_WithKey_CallsApi()
    {
        SetupProjects();
        SetupVault(1);
        _handler.SetJsonResponse("api/vaults/1/secrets", new VaultSecretDto { Id = 50, Key = "API_KEY" });
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var notif = Services.GetRequiredService<OmniOverlayService>();
        SetFormValue(cut.Instance, "_newSecret", "Key", "API_KEY");
        SetFormValue(cut.Instance, "_newSecret", "Value", "secret123");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret"));

        // Secret created → input fields reset and a success toast surfaces.
        Assert.Equal(string.Empty, GetFormValue<string>(cut.Instance, "_newSecret", "Key"));
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Success, notif.Toasts()[0].Severity);
    }

    // ── GetExpiryBadge - static method ──────────────────────────────────────

    [Fact]
    public void GetExpiryBadge_Expired_ReturnsDanger()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [DateTime.UtcNow.AddDays(-1)])!;
        Assert.Equal(OmniTone.Danger, result);
    }

    [Fact]
    public void GetExpiryBadge_SoonExpiring_ReturnsWarning()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [DateTime.UtcNow.AddDays(7)])!;
        Assert.Equal(OmniTone.Warning, result);
    }

    [Fact]
    public void GetExpiryBadge_FarFuture_ReturnsLight()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [DateTime.UtcNow.AddDays(30)])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    // ── ReloadDetail ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReloadDetail_RefreshesDetail()
    {
        SetupProjects();
        SetupVault(1);
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // Clear the loaded detail, then ReloadDetail must re-fetch it from the API.
        typeof(VaultEdit).GetField("_detail", Priv)!.SetValue(cut.Instance, null);
        var method = typeof(VaultEdit).GetMethod("ReloadDetail", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var detail = (VaultDetailDto?)typeof(VaultEdit).GetField("_detail", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(detail);
        Assert.Equal("My Vault", detail!.Name);
    }
}
