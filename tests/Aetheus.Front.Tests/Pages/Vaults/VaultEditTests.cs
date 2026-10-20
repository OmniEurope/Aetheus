// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

public class VaultEditTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public VaultEditTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_NewVaultPage()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });

        var cut = Render<VaultEdit>();
        Assert.Contains("NewVault", cut.Markup);
    }

    [Fact]
    public void NewVault_NameInputUpdatesTheFormModel()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });

        var cut = Render<VaultEdit>();
        cut.Find("input#vault-name").Input("observability-package-publication");

        var model = typeof(VaultEdit)
            .GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        var name = model.GetType().GetProperty("Name")!.GetValue(model);
        Assert.Equal("observability-package-publication", name);
    }

    [Fact]
    public void Renders_ExistingVaultPage()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Project1" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "Production Vault",
            Description = "Production secrets",
            ProjectId = 1,
            ProjectName = "Project1",
            RowVersion = Guid.NewGuid(),
            Secrets =
            [
                new VaultSecretDto { Id = 1, Key = "DB_PASSWORD", CreatedAt = DateTime.UtcNow }
            ]
        });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        Assert.Contains("Production Vault", cut.Markup);
    }

    [Fact]
    public void Renders_NewVaultPage_WithProjects()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Project1" }],
            TotalCount = 1
        });

        var cut = Render<VaultEdit>();
        Assert.Contains("NewVault", cut.Markup);
    }

    [Fact]
    public void Renders_VaultWithMultipleSecrets()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "Dev Vault",
            RowVersion = Guid.NewGuid(),
            Secrets =
            [
                new VaultSecretDto { Id = 1, Key = "API_KEY", CreatedAt = DateTime.UtcNow },
                new VaultSecretDto { Id = 2, Key = "DB_PASS", CreatedAt = DateTime.UtcNow },
                new VaultSecretDto { Id = 3, Key = "JWT_SECRET", CreatedAt = DateTime.UtcNow }
            ]
        });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        // All three secret keys render as clickable cells in the secrets grid.
        Assert.Contains("API_KEY", cut.Markup);
        Assert.Contains("DB_PASS", cut.Markup);
        Assert.Contains("JWT_SECRET", cut.Markup);
    }

    [Fact]
    public async Task AddSecret_WithEmptyKey_MakesNoRequest()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "Test",
            RowVersion = Guid.NewGuid(),
            Secrets = []
        });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));

        await InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret");

        // Empty key short-circuits before the create call - no secret POST is sent.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/vaults/1/secrets"));
    }

    [Fact]
    public void Renders_SecretExpiryBadges()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "Expiry Test",
            RowVersion = Guid.NewGuid(),
            Secrets =
            [
                new VaultSecretDto { Id = 1, Key = "EXPIRING", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(3) },
                new VaultSecretDto { Id = 2, Key = "EXPIRED", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(-1) },
                new VaultSecretDto { Id = 3, Key = "OK", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30) }
            ]
        });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        // Each secret with an ExpiresAt renders a badge showing the formatted expiry date.
        Assert.Contains("EXPIRING", cut.Markup);
        // In the user's culture, not ISO: a French user reads 05/10/2026.
        Assert.Contains(DateTime.UtcNow.AddDays(3).ToString("d"), cut.Markup);
        Assert.Contains(DateTime.UtcNow.AddDays(-1).ToString("d"), cut.Markup);
    }

    [Theory]
    [InlineData(-1, OmniTone.Danger)]
    [InlineData(7, OmniTone.Warning)]
    [InlineData(30, OmniTone.Neutral)]
    public void GetExpiryBadge_ReturnsExpected(int daysFromNow, OmniTone expected)
    {
        var method = typeof(VaultEdit).GetMethod("GetExpiryBadge", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = (OmniTone)method.Invoke(null, [DateTime.UtcNow.AddDays(daysFromNow)])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task AddSecret_WithValidKey_CallsApi()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "Test",
            RowVersion = Guid.NewGuid(),
            Secrets = []
        });
        _handler.SetJsonResponse("api/vaults/1/secrets", new VaultSecretDto { Id = 1, Key = "NEW_KEY" });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));

        SetFormValue(cut.Instance, "_newSecret", "Key", "NEW_KEY");
        SetFormValue(cut.Instance, "_newSecret", "Value", "secret_value");
        await InvokeFormSubmitAsync(cut.Instance, "AddSecret", "_newSecret");

        // A valid key drives a real POST to the vault's secrets endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/vaults/1/secrets"));
    }

    [Fact]
    public void Renders_VaultDeleteButton_ForExisting()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/vaults/1", new VaultDetailDto
        {
            Id = 1,
            Name = "DeleteMe",
            RowVersion = Guid.NewGuid(),
            Secrets = []
        });

        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(
            () => typeof(VaultEdit).GetField("_detail", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(cut.Instance) is not null);

        // Only existing vaults expose Delete in the tools menu. Open the menu before locating the
        // localized action and delete icon.
        cut.Find(".omni-overflow-menu__trigger").Click();
        Assert.Contains(cut.FindAll(".omni-menu__item"), item => item.TextContent.Contains("Delete"));
    }

    private void SetupExistingVault(int id = 1)
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [new ProjectDto { Id = 1, Name = "P1" }], TotalCount = 1 });
        _handler.SetJsonResponse($"api/vaults/{id}", new VaultDetailDto
        {
            Id = id,
            Name = "Test Vault",
            Description = "desc",
            ProjectId = 1,
            ProjectName = "P1",
            RowVersion = Guid.NewGuid(),
            Secrets = [new VaultSecretDto { Id = 1, Key = "KEY1", CreatedAt = DateTime.UtcNow }]
        });
    }

    [Fact]
    public async Task OnSubmit_NewVault_CallsCreate()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/vaults", new VaultDto { Id = 5, Name = "New" });
        var cut = Render<VaultEdit>();
        var model = typeof(VaultEdit).GetNestedType("VaultModel", BindingFlags.NonPublic)!;
        var inst = typeof(VaultEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        model.GetProperty("Name")!.SetValue(inst, "New Vault");
        var method = typeof(VaultEdit).GetMethod("OnSubmit", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // New-vault submit POSTs the create request to the vaults collection endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/vaults"));
    }

    [Fact]
    public async Task OnSubmit_ExistingVault_CallsUpdate()
    {
        SetupExistingVault();
        _handler.SetJsonResponse("api/vaults/1", new VaultDto { Id = 1, Name = "Updated" });
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        var method = typeof(VaultEdit).GetMethod("OnSubmit", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Existing-vault submit PUTs the update request to the vault's own endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/vaults/1"));
    }

    [Fact]
    public async Task ExportKeys_CallsJsInterop()
    {
        SetupExistingVault();
        _handler.SetJsonResponse("api/vaults/1/export-keys", new List<string> { "KEY1" });
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        var method = typeof(VaultEdit).GetMethod("ExportKeys", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // ExportKeys fetches the key list, then hands it to the JS downloadFile helper.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("export-keys"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "downloadFile");
    }

    [Fact]
    public async Task CopyKeyReference_CallsClipboard()
    {
        SetupExistingVault();
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 1));
        var method = typeof(VaultEdit).GetMethod("CopyKeyReference", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["KEY1"])!);

        // The reference is copied to the clipboard in the $(key) interpolation form.
        var clip = Assert.Single(JSInterop.Invocations, i => i.Identifier == "navigator.clipboard.writeText");
        Assert.Equal("$(KEY1)", clip.Arguments[0]);
    }

    [Fact]
    public void Renders_EmptyVault_NoSecrets()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/vaults/2", new VaultDetailDto
        {
            Id = 2,
            Name = "Empty",
            RowVersion = Guid.NewGuid(),
            Secrets = []
        });
        var cut = Render<VaultEdit>(p => p.Add(x => x.Id, 2));
        // Existing vault with no secrets still renders its name and the empty secrets grid section.
        Assert.Contains("Empty", cut.Markup);
        Assert.Contains("Secrets", cut.Markup);
    }
}
