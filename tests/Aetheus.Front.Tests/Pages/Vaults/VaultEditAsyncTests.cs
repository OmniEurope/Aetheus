// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Vaults;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Async method coverage for VaultEdit.razor.cs - OnSubmit create/update paths,
/// AddSecret, ExportKeys, GetExpiryBadge, CopyKeyReference, ReloadDetail.
/// Dialog.Confirm (Delete/RotateSecret) and Dialog.OpenAsync (ImportSecrets, UpdateSecret,
/// ShowVersions) are excluded - they hang in bUnit.
/// </summary>
public class VaultEditAsyncTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public VaultEditAsyncTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupProjects()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "TestProject" }, new ProjectDto { Id = 2, Name = "OtherProject" }],
            TotalCount = 2
        });
    }

    private void SetupVault(int id, string name = "my-vault")
    {
        _handler.SetJsonResponse($"api/vaults/{id}", new VaultDetailDto
        {
            Id = id,
            Name = name,
            Description = "Vault description",
            ProjectId = 1,
            ProjectName = "TestProject",
            RowVersion = Guid.NewGuid(),
            Secrets =
            [
                new VaultSecretDto { Id = 10, Key = "API_KEY", CreatedAt = DateTime.UtcNow },
                new VaultSecretDto { Id = 11, Key = "DB_PASS", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30) }
            ]
        });
    }

    // ── Test 1: New vault - OnSubmit creates and navigates ───────────────────

    [Fact]
    public async Task OnSubmit_NewVault_CallsCreateApi()
    {
        SetupProjects();
        _handler.SetJsonResponse("api/vaults", new VaultDetailDto { Id = 99, Name = "created-vault" });

        var cut = Render<VaultEdit>();

        // Set model fields
        typeof(VaultEdit)
            .GetNestedType("VaultModel", Priv)!
            .GetProperty("Name")!
            .SetValue(typeof(VaultEdit).GetField("_model", Priv)!.GetValue(cut.Instance), "created-vault");

        var method = typeof(VaultEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // The new-vault path POSTs to api/vaults (the "Create" call the name promises).
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/vaults"));
    }

    // ── Test 2: Existing vault - OnSubmit updates ────────────────────────────

    [Fact]
    public async Task OnSubmit_ExistingVault_CallsUpdateApi()
    {
        SetupProjects();
        SetupVault(50);
        _handler.SetJsonResponse("api/vaults/50", new VaultDetailDto { Id = 50, Name = "updated-vault", RowVersion = Guid.NewGuid(), Secrets = [] });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 50));
        cut.WaitForState(
            () => typeof(VaultEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        var method = typeof(VaultEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // The edit path PUTs to api/vaults/50 (the "Update" call the name promises).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/vaults/50"));
    }

    // ── Test 3: AddSecret with valid key ─────────────────────────────────────

    [Fact]
    public async Task AddSecret_ValidKey_ClearsFieldsAfterSuccess()
    {
        SetupProjects();
        SetupVault(51);
        _handler.SetJsonResponse("api/vaults/51/secrets", new VaultSecretDto { Id = 100, Key = "NEW_KEY" });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 51));
        cut.WaitForState(
            () => typeof(VaultEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        SetFormValue(cut.Instance, "_newSecret", "Key", "NEW_KEY");
        SetFormValue(cut.Instance, "_newSecret", "Value", "secret-value");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret"));

        var key = GetFormValue<string>(cut.Instance, "_newSecret", "Key");
        Assert.Equal(string.Empty, key);
    }

    // ── Test 4: AddSecret null API response leaves state intact ──────────────

    [Fact]
    public async Task AddSecret_ApiReturnsNull_KeyNotCleared()
    {
        SetupProjects();
        SetupVault(52);
        // No stub for /secrets POST - default handler returns {}
        // which deserializes as empty VaultSecretDto (not null), so test
        // for non-null API response path. Use explicit null return:
        _handler.SetResponse("api/vaults/52/secrets", System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 52));
        cut.WaitForState(
            () => typeof(VaultEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        SetFormValue(cut.Instance, "_newSecret", "Key", "WILL_FAIL");
        SetFormValue(cut.Instance, "_newSecret", "Value", "secret-value");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret"));

        // The POST was attempted, but the 500 response → secret is null → key field is NOT cleared.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/vaults/52/secrets"));
        Assert.Equal("WILL_FAIL", GetFormValue<string>(cut.Instance, "_newSecret", "Key"));
    }

    // ── Test 5: AddSecret empty key is a no-op ───────────────────────────────

    [Fact]
    public async Task AddSecret_EmptyKey_IsNoop()
    {
        SetupProjects();
        SetupVault(53);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 53));
        SetFormValue(cut.Instance, "_newSecret", "Key", "");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret"));

        // Empty key short-circuits before the create call - no secret POST is sent.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/vaults/53/secrets"));
    }

    // ── Test 6: ExportKeys calls JS download ─────────────────────────────────

    [Fact]
    public async Task ExportKeys_CallsDownloadJs()
    {
        SetupProjects();
        SetupVault(54);
        _handler.SetJsonResponse("api/vaults/54/export-keys", new List<string> { "API_KEY", "DB_PASS" });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 54));
        cut.WaitForState(
            () => typeof(VaultEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        var method = typeof(VaultEdit).GetMethod("ExportKeys", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // ExportKeys fetches the key list, then hands it to the JS downloadFile helper.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/vaults/54/export-keys"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "downloadFile");
    }

    // ── Test 7: GetExpiryBadge - expired ────────────────────────────────────

    [Fact]
    public void GetExpiryBadge_PastDate_ReturnsDanger()
    {
        SetupProjects();
        SetupVault(55);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 55));

        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var style = method.Invoke(null, [DateTime.UtcNow.AddDays(-1)])!.ToString()!;
        Assert.Contains("Danger", style);
    }

    // ── Test 8: GetExpiryBadge - expiring soon ───────────────────────────────

    [Fact]
    public void GetExpiryBadge_ExpiresIn7Days_ReturnsWarning()
    {
        SetupProjects();
        SetupVault(56);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 56));

        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var style = method.Invoke(null, [DateTime.UtcNow.AddDays(7)])!.ToString()!;
        Assert.Contains("Warning", style);
    }

    // ── Test 9: GetExpiryBadge - expires far in future ───────────────────────

    [Fact]
    public void GetExpiryBadge_FarFutureDate_ReturnsLight()
    {
        SetupProjects();
        SetupVault(57);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 57));

        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var style = method.Invoke(null, [DateTime.UtcNow.AddDays(60)])!.ToString()!;
        Assert.Contains("Neutral", style);
    }

    // ── Test 10: CopyKeyReference calls JS clipboard ─────────────────────────

    [Fact]
    public async Task CopyKeyReference_InvokesJsClipboard()
    {
        SetupProjects();
        SetupVault(58);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 58));
        cut.WaitForState(
            () => typeof(VaultEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        var method = typeof(VaultEdit).GetMethod("CopyKeyReference", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["API_KEY"])!);

        // The reference is copied to the clipboard in the $(key) interpolation form.
        var clip = Assert.Single(JSInterop.Invocations, i => i.Identifier == "navigator.clipboard.writeText");
        Assert.Equal("$(API_KEY)", clip.Arguments[0]);
    }

    // ── Test 11: ReloadDetail updates _detail from API ───────────────────────

    [Fact]
    public async Task ReloadDetail_UpdatesDetail()
    {
        SetupProjects();
        SetupVault(59, "reloaded-vault");

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 59));
        cut.WaitForState(
            () => typeof(VaultEdit).GetField("_detail", Priv)!.GetValue(cut.Instance) != null);

        var method = typeof(VaultEdit).GetMethod("ReloadDetail", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var detail = (VaultDetailDto)typeof(VaultEdit).GetField("_detail", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("reloaded-vault", detail.Name);
    }

    // ── Test 12: New vault binds the ProjectId query param into the model ────

    [Fact]
    public void NewVault_WithProjectIdQueryParam_PrefillsModelProjectId()
    {
        SetupProjects();

        // The ?ProjectId=1 query binding pre-selects the owning project when it exists in the loaded
        // list - exercises OnParametersSetAsync's real binding path, not a set-then-get of the field.
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("http://localhost/?ProjectId=1");

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(
            () => (typeof(VaultEdit).GetField("_projects", Priv)!.GetValue(cut.Instance) as System.Collections.ICollection)?.Count > 0);

        var model = typeof(VaultEdit).GetField("_model", Priv)!.GetValue(cut.Instance);
        var pid = (int?)model!.GetType().GetProperty("ProjectId")!.GetValue(model);
        Assert.Equal(1, pid);
    }

    // ── Test 13: OnSubmit create - null response doesn't crash ───────────────

    [Fact]
    public async Task OnSubmit_CreateReturnsNull_SavingSetFalse()
    {
        SetupProjects();
        _handler.SetResponse("api/vaults", System.Net.HttpStatusCode.BadRequest);

        var cut = Render<VaultEdit>();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var method = typeof(VaultEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // A 400 create response yields a null vault - the guard must skip navigation to any
        // /vaults/{id} detail route (the create only navigates on success).
        Assert.DoesNotContain(nav.History, h => System.Text.RegularExpressions.Regex.IsMatch(h.Uri, @"vaults/\d+"));
        Assert.False((bool)typeof(VaultEdit).GetField("_saving", Priv)!.GetValue(cut.Instance)!);
    }
}
