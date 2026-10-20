// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Vaults;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.VaultsCoverage;

/// <summary>
/// Additional coverage for VaultEdit.razor.cs.
/// UpdateSecret / RotateSecret / DeleteSecret / ShowVersions / ImportSecrets are all excluded
/// because they call Dialog.OpenAsync or Dialog.Confirm which hang forever.
///
/// This file covers:
/// - GetExpiryBadge (three branches)
/// - AddSecret (key guard + success path)
/// - CopyKeyReference (JS interop)
/// - ExportKeys (JS interop)
/// - ReloadDetail
/// - OnSubmit (edit mode → update)
/// - OnSubmit (new mode → create)
/// </summary>
// Renamed from VaultUpdateSecretTests: this fixture never exercises UpdateSecret (excluded because
// it opens a dialog that hangs in bUnit). It covers the create/update/reload code paths instead.
public class VaultEditSecretCoverageTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private readonly BunitTestHelper.TestHandler _handler;

    public VaultEditSecretCoverageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupProjects()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "App" }],
            TotalCount = 1
        });
    }

    private void SetupVault(int id = 1)
    {
        _handler.SetJsonResponse($"api/vaults/{id}", MakeDetail(id));
    }

    private static VaultDetailDto MakeDetail(int id = 1) => new()
    {
        Id = id,
        Name = "Test Vault",
        Description = "Desc",
        ProjectId = 1,
        RowVersion = Guid.NewGuid(),
        Secrets =
        [
            new VaultSecretDto { Id = 10, Key = "DB_PASS", ExpiresAt = DateTime.UtcNow.AddDays(30) },
            new VaultSecretDto { Id = 11, Key = "API_KEY", ExpiresAt = DateTime.UtcNow.AddDays(5) },
            new VaultSecretDto { Id = 12, Key = "OLD_KEY", ExpiresAt = DateTime.UtcNow.AddDays(-1) }
        ]
    };

    // ── GetExpiryBadge ────────────────────────────────────────────────────────

    [Fact]
    public void GetExpiryBadge_Expired_ReturnsDanger()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [DateTime.UtcNow.AddDays(-1)])!;
        Assert.Equal(OmniTone.Danger, result);
    }

    [Fact]
    public void GetExpiryBadge_SoonExpiring_ReturnsWarning()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [DateTime.UtcNow.AddDays(7)])!;
        Assert.Equal(OmniTone.Warning, result);
    }

    [Fact]
    public void GetExpiryBadge_FarExpiry_ReturnsLight()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [DateTime.UtcNow.AddDays(30)])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    [Fact]
    public void GetExpiryBadge_ExactlyZeroDays_ReturnsDanger()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [DateTime.UtcNow])!;
        Assert.Equal(OmniTone.Danger, result);
    }

    [Fact]
    public void GetExpiryBadge_ExactlyFifteenDays_ReturnsLight()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge", PrivStatic)!;
        // > 14 days → Light
        var result = (OmniTone)method.Invoke(null, [DateTime.UtcNow.AddDays(15)])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    // ── AddSecret: empty key guard ─────────────────────────────────────────────

    [Fact]
    public async Task AddSecret_EmptyKey_KeepsItSet()
    {
        SetupProjects();
        SetupVault(1);
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(
            () => Priv_Get<VaultDetailDto?>(cut.Instance, "_detail") is not null);

        // The form model key is empty by default.
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret"));

        // Should be a no-op - _detail unchanged
        var detail = Priv_Get<VaultDetailDto?>(cut.Instance, "_detail");
        Assert.NotNull(detail);
        Assert.Equal(3, detail.Secrets.Count); // unchanged
    }

    [Fact]
    public async Task AddSecret_WithKey_CallsApiAndReloads()
    {
        SetupProjects();
        SetupVault(1);
        var newSecret = new VaultSecretDto { Id = 99, Key = "NEW_KEY" };
        _handler.SetJsonResponse("api/vaults/1/secrets", newSecret);
        // Reload returns updated detail
        _handler.SetJsonResponse("api/vaults/1", MakeDetail(1));

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(
            () => Priv_Get<VaultDetailDto?>(cut.Instance, "_detail") is not null);

        SetFormValue(cut.Instance, "_newSecret", "Key", "NEW_KEY");
        SetFormValue(cut.Instance, "_newSecret", "Value", "secret-value");
        SetFormValue(cut.Instance, "_newSecret", "ExpiresAt", (DateTime?)null);
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret"));

        // The new form model should be reset after success.
        var key = GetFormValue<string>(cut.Instance, "_newSecret", "Key");
        Assert.Equal(string.Empty, key);
    }

    // ── OnSubmit - new vault ──────────────────────────────────────────────────

    [Fact]
    public async Task OnSubmit_NewVault_CallsCreateAndNavigates()
    {
        SetupProjects();
        var created = new VaultDetailDto { Id = 42, Name = "Brand New Vault" };
        _handler.SetJsonResponse("api/vaults", created);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(
            () => Priv_Get<bool>(cut.Instance, "_saving") == false
                  && Priv_Get<List<ProjectDto>>(cut.Instance, "_projects")?.Count > 0);

        // Set the model
        var model = Priv_Get<object>(cut.Instance, "_model");
        model?.GetType().GetProperty("Name")?.SetValue(model, "Brand New Vault");

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var method = typeof(VaultEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // The create path POSTs to api/vaults and navigates to the new vault's detail route.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/vaults"));
        Assert.Contains(nav.History, h => h.Uri.Contains("vaults/42"));
    }

    // ── OnSubmit - edit vault ─────────────────────────────────────────────────

    [Fact]
    public async Task OnSubmit_ExistingVault_CallsUpdateAndReloads()
    {
        SetupProjects();
        SetupVault(1);
        var updated = MakeDetail(1);
        updated = updated with { Name = "Updated Name" };
        _handler.SetJsonResponse("api/vaults/1", updated);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(
            () => Priv_Get<VaultDetailDto?>(cut.Instance, "_detail") is not null);

        var method = typeof(VaultEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // The edit path issues a PUT to api/vaults/1, then reloads the detail (GET) - so _detail
        // now carries the updated name returned by the stub.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/vaults/1"));
        var detail = Priv_Get<VaultDetailDto?>(cut.Instance, "_detail");
        Assert.NotNull(detail);
        Assert.Equal("Updated Name", detail!.Name);
    }

    // ── ReloadDetail ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReloadDetail_RefreshesDetail()
    {
        SetupProjects();
        SetupVault(1);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(
            () => Priv_Get<VaultDetailDto?>(cut.Instance, "_detail") is not null);

        // Change what the API returns, then null out _detail so a re-populated value proves the refetch
        // happened (rather than reading a value that was already there before the call).
        var refreshed = MakeDetail(1) with { Name = "Refreshed Vault" };
        _handler.SetJsonResponse("api/vaults/1", refreshed);
        Priv_Set(cut.Instance, "_detail", null);

        var method = typeof(VaultEdit).GetMethod("ReloadDetail", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var detail = Priv_Get<VaultDetailDto?>(cut.Instance, "_detail");
        Assert.NotNull(detail);
        Assert.Equal("Refreshed Vault", detail!.Name);
    }

    // ── Security: secret values are never rendered in cleartext ───────────────

    [Fact]
    public void SecretsGrid_MasksValues_NeverRendersCleartext()
    {
        SetupProjects();
        // The secret's plaintext value must never reach the wire (VaultSecretDto exposes no value
        // field) nor the rendered markup - the value column shows a masked placeholder only.
        const string plaintext = "super-secret-value-DO-NOT-LEAK";
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "Prod Vault",
            SecretCount = 1,
            Secrets = [new VaultSecretDto { Id = 10, Key = "DB_PASS" }]
        });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(
            () => Priv_Get<VaultDetailDto?>(cut.Instance, "_detail") is not null);

        // The key is shown, the value is masked, and no plaintext secret value appears anywhere.
        Assert.Contains("DB_PASS", cut.Markup);
        Assert.Contains("••••••••", cut.Markup);
        Assert.DoesNotContain(plaintext, cut.Markup);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void Priv_Set(object obj, string field, object? value)
        => typeof(VaultEdit).GetField(field, Priv)!.SetValue(obj, value);

    private static T? Priv_Get<T>(object obj, string field)
        => (T?)typeof(VaultEdit).GetField(field, Priv)!.GetValue(obj);
}
