// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Vaults;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

public class VaultEditRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public VaultEditRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static VaultDetailDto BuildVaultDetail() => new()
    {
        Id = 1,
        Name = "prod-secrets",
        Description = "Production secrets",
        ProjectId = 1,
        ProjectName = "MyProject",
        RowVersion = Guid.NewGuid(),
        Secrets =
        [
            new VaultSecretDto { Id = 10, Key = "DB_PASSWORD", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new VaultSecretDto { Id = 11, Key = "API_KEY", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30) }
        ]
    };

    private static PaginatedResult<ProjectDto> BuildProjects() => new()
    {
        Items = [new ProjectDto { Id = 1, Name = "MyProject" }],
        TotalCount = 1
    };

    private void SetupStubs(int vaultId = 1)
    {
        _handler.SetJsonResponse("api/projects", BuildProjects());
        _handler.SetJsonResponse($"api/vaults/{vaultId}", BuildVaultDetail());
    }

    [Fact]
    public void Renders_ExistingVault_WithSecrets()
    {
        SetupStubs();

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Contains("DB_PASSWORD"));

        // The loaded vault's secret keys are rendered in the grid (never their values).
        Assert.Contains("DB_PASSWORD", cut.Markup);
        Assert.Contains("API_KEY", cut.Markup);
    }

    [Fact]
    public void Renders_NewVault_WithoutId()
    {
        _handler.SetJsonResponse("api/projects", BuildProjects());

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        // New-vault mode loads the project list but must never fetch a vault detail.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/projects"));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("api/vaults/"));
    }

    [Fact]
    public void IsNew_IsTrueWhenIdIsNull()
    {
        _handler.SetJsonResponse("api/projects", BuildProjects());

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var isNew = (bool)typeof(VaultEdit)
            .GetProperty("_isNew", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.True(isNew);
    }

    [Fact]
    public void IsNew_IsFalseWhenIdIsSet()
    {
        SetupStubs();

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var isNew = (bool)typeof(VaultEdit)
            .GetProperty("_isNew", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.False(isNew);
    }

    [Fact]
    public async Task AddSecret_WithEmptyKey_MakesNoRequest()
    {
        SetupStubs();

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50);

        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret"));

        // Empty key short-circuits before any API call - no secret POST is sent.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task ExportKeys_InvokesJsDownload()
    {
        SetupStubs();
        // ExportVaultSecretKeysAsync fetches api/vaults/{id}/export-keys (no /secrets/ segment)
        _handler.SetJsonResponse("api/vaults/1/export-keys", new List<string> { "DB_PASSWORD", "API_KEY" });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var method = typeof(VaultEdit).GetMethod("ExportKeys", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // ExportKeys fetches the key list, then hands it to the JS downloadFile helper.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("export-keys"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "downloadFile");
    }

    [Fact]
    public void GetExpiryBadge_ReturnsCorrectStyle()
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge", BindingFlags.NonPublic | BindingFlags.Static)!;

        var dangerResult = method.Invoke(null, [DateTime.UtcNow.AddDays(-1)]);
        var warningResult = method.Invoke(null, [DateTime.UtcNow.AddDays(7)]);
        var lightResult = method.Invoke(null, [DateTime.UtcNow.AddDays(30)]);

        Assert.Equal(OmniTone.Danger, dangerResult);
        Assert.Equal(OmniTone.Warning, warningResult);
        Assert.Equal(OmniTone.Neutral, lightResult);
    }

    [Fact]
    public async Task CopyKeyReference_CallsJs()
    {
        SetupStubs();

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var method = typeof(VaultEdit).GetMethod("CopyKeyReference", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["DB_PASSWORD"])!);

        // The reference is copied to the clipboard in the $(key) interpolation form.
        var clip = Assert.Single(JSInterop.Invocations, i => i.Identifier == "navigator.clipboard.writeText");
        Assert.Equal("$(DB_PASSWORD)", clip.Arguments[0]);
    }
}
