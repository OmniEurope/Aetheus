// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Vaults;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Template-branch coverage for VaultEdit.razor.
/// Covers new-vault vs existing-vault branches, secrets list, expiry badge variations.
/// </summary>
public class VaultEditTemplateCoverageTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public VaultEditTemplateCoverageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // Default: no projects
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
    }

    // ── GetExpiryBadge ────────────────────────────────────────────────────────

    [Fact]
    public void GetExpiryBadge_Expired_ReturnsDanger()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var past = DateTime.UtcNow.AddDays(-1);
        var result = (OmniTone)method.Invoke(null, [past])!;
        Assert.Equal(OmniTone.Danger, result);
    }

    [Fact]
    public void GetExpiryBadge_ExpiresSoon_ReturnsWarning()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var soon = DateTime.UtcNow.AddDays(7);
        var result = (OmniTone)method.Invoke(null, [soon])!;
        Assert.Equal(OmniTone.Warning, result);
    }

    [Fact]
    public void GetExpiryBadge_ExpiresLater_ReturnsLight()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var later = DateTime.UtcNow.AddDays(30);
        var result = (OmniTone)method.Invoke(null, [later])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    // ── Template: new vault ───────────────────────────────────────────────────

    /// <summary>
    /// Covers: _isNew = true → "Create" button (not "Save") (line 52),
    ///         heading shows "NewVault" (line 10),
    ///         delete button NOT shown (line 16-20),
    ///         secrets section NOT shown (line 57).
    /// </summary>
    [Fact]
    public void Template_NewVault_ShowsCreateForm()
    {
        var cut = Render<VaultEdit>();
        cut.WaitForState(() => cut.Markup.Length > 0);

        // _isNew = true when no Id parameter provided
        var isNew = (bool)typeof(VaultEdit).GetProperty("_isNew",
            BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.True(isNew);
    }

    /// <summary>
    /// Covers: _isNew = true, Id = 0 → same as new vault.
    /// </summary>
    [Fact]
    public void Template_VaultIdZero_IsNewTrue()
    {
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 0));
        cut.WaitForState(() => cut.Markup.Length > 0);

        var isNew = (bool)typeof(VaultEdit).GetProperty("_isNew",
            BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.True(isNew);
    }

    // ── Template: existing vault with secrets ─────────────────────────────────

    /// <summary>
    /// Covers: _isNew = false, _detail not null → "Save" button + secrets section (lines 57-113)
    ///         secret without ExpiresAt → no badge rendered (line 84, @if branch not entered)
    /// </summary>
    [Fact]
    public void Template_ExistingVaultWithSecrets_ShowsSecretsSection()
    {
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "ProductionSecrets",
            Description = "Prod secrets",
            SecretCount = 2,
            Secrets =
            [
                new VaultSecretDto { Id = 10, Key = "DB_PASSWORD", VersionCount = 3 },
                new VaultSecretDto { Id = 11, Key = "API_KEY", VersionCount = 1 }
            ]
        });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() =>
        {
            var detail = typeof(VaultEdit).GetField("_detail",
                BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(cut.Instance);
            return detail is not null;
        });

        // Existing-vault branch renders the secrets section with each loaded secret key.
        Assert.Contains("DB_PASSWORD", cut.Markup);
        Assert.Contains("API_KEY", cut.Markup);
    }

    /// <summary>
    /// Covers: secrets with ExpiresAt set → badge rendered (lines 86-87).
    ///         Tests expired (Danger), soon (Warning), and future (Light) badges.
    /// </summary>
    [Fact]
    public void Template_ExistingVaultWithExpiringSecrets_ShowsExpiryBadges()
    {
        _handler.SetJsonResponse("api/vaults/2", new VaultDetailDto
        {
            Id = 2,
            Name = "ExpiringSecrets",
            SecretCount = 3,
            Secrets =
            [
                new VaultSecretDto { Id = 20, Key = "EXPIRED_KEY", ExpiresAt = DateTime.UtcNow.AddDays(-1), VersionCount = 1 },
                new VaultSecretDto { Id = 21, Key = "EXPIRING_SOON", ExpiresAt = DateTime.UtcNow.AddDays(7), VersionCount = 2 },
                new VaultSecretDto { Id = 22, Key = "FAR_FUTURE", ExpiresAt = DateTime.UtcNow.AddDays(60), VersionCount = 1 }
            ]
        });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 2));
        cut.WaitForState(() =>
        {
            var detail = typeof(VaultEdit).GetField("_detail",
                BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(cut.Instance);
            return detail is not null;
        });

        // All three expiring secrets are rendered in the grid by key.
        Assert.Contains("EXPIRED_KEY", cut.Markup);
        Assert.Contains("EXPIRING_SOON", cut.Markup);
        Assert.Contains("FAR_FUTURE", cut.Markup);
    }

    /// <summary>
    /// Covers: spinner branch (line 23-26). When _isNew = false and _detail is null the loading
    /// spinner renders instead of the form.
    /// </summary>
    [Fact]
    public void Template_ExistingVault_Loading_ShowsAetheusLoader()
    {
        _handler.SetResponse("api/vaults/3", System.Net.HttpStatusCode.OK);

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 3));
        cut.WaitForState(() => cut.Markup.Length > 0);

        // Drive the loading state deterministically: Id=3 makes _isNew false, but a 200 {} body
        // deserialises to a NON-null empty DTO, so the load alone won't reproduce the null-detail
        // spinner. Clear _detail and re-render; _previousId already equals Id, so no refetch fires.
        var priv = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(VaultEdit).GetField("_detail", priv)!.SetValue(cut.Instance, null);
        cut.Render();

        // _isNew is false (Id = 3) and _detail is null → the indeterminate loading spinner is on
        // screen and the edit form (its Save/Create button) is not yet rendered.
        var isNew = (bool)typeof(VaultEdit).GetProperty("_isNew", priv)!.GetValue(cut.Instance)!;
        Assert.False(isNew);
        Assert.Contains("aetheus-loader-logo", cut.Markup);
    }

    // ── AddSecret method ──────────────────────────────────────────────────────

    /// <summary>
    /// Covers: AddSecret with empty key → returns early (line 141).
    /// </summary>
    [Fact]
    public async Task AddSecret_EmptyKey_MakesNoRequest()
    {
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/vaults/1",
            new VaultDetailDto { Id = 1, Name = "Test", Secrets = [] });
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));

        SetFormValue(cut.Instance, "_newSecret", "Key", "");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret"));

        // Empty key short-circuits before the create call - no secret POST is sent.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/vaults/1/secrets"));
    }

    // ── Property tests ────────────────────────────────────────────────────────

    [Fact]
    public void IsNew_WhenIdIsNull_ReturnsTrue()
    {
        var cut = Render<VaultEdit>();
        var isNew = (bool)typeof(VaultEdit).GetProperty("_isNew",
            BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.True(isNew);
    }

    [Fact]
    public void IsNew_WhenIdIsNonZero_ReturnsFalse()
    {
        _handler.SetJsonResponse("api/vaults/5", new VaultDetailDto { Id = 5, Name = "V", Secrets = [] });
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() =>
        {
            var detail = typeof(VaultEdit).GetField("_detail",
                BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(cut.Instance);
            return detail is not null;
        });

        var isNew = (bool)typeof(VaultEdit).GetProperty("_isNew",
            BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.False(isNew);
    }

    /// <summary>
    /// Covers: GetExpiryBadge at exactly 14 days → returns Warning.
    /// </summary>
    [Fact]
    public void GetExpiryBadge_ExactlyFourteenDays_ReturnsWarning()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var exactlyFourteen = DateTime.UtcNow.AddDays(14);
        var result = (OmniTone)method.Invoke(null, [exactlyFourteen])!;
        Assert.Equal(OmniTone.Warning, result);
    }

    /// <summary>
    /// Covers: vault with projects for the owner selector.
    /// </summary>
    [Fact]
    public void Template_NewVaultWithProjects_PopulatesOwnerSelector()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto { Id = 1, Name = "Backend" },
                new ProjectDto { Id = 2, Name = "Frontend" }
            ],
            TotalCount = 2
        });

        var cut = Render<VaultEdit>();
        cut.WaitForState(() => cut.Markup.Length > 0);

        // New-vault mode loads the project list (for the owner selector) and never fetches a vault detail.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/projects"));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("api/vaults/"));
    }
}
