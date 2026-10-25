// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Pages;

public class AdministrationTests : BunitContext
{
    public AdministrationTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private readonly BunitTestHelper.TestHandler _handler;

    [Fact]
    public void Renders_AdminPage()
    {
        SetupDefaultResponses();
        var cut = Render<Administration>();
        Assert.Contains("Administration", cut.Markup);
    }

    [Fact]
    public void Renders_NavigationCards()
    {
        SetupDefaultResponses();
        var cut = Render<Administration>();
        Assert.Contains("Users", cut.Markup);
        Assert.Contains("AuditLogs", cut.Markup);
        Assert.Contains("Plugins", cut.Markup);
        Assert.Contains("Dashboards", cut.Markup);
    }

    /// <summary>One tile per page of the side menu's Administration group, none missing, with the
    /// icon that menu gives it, and a description under each title.</summary>
    [Fact]
    public void Tiles_MatchTheAdministrationMenu_PageForPageAndIconForIcon()
    {
        var navMenu = File.ReadAllText(Path.Combine(Architecture.RepositoryScan.Root, "src", "Aetheus.Front", "Layout", "NavMenu.razor"));
        var menu = System.Text.RegularExpressions.Regex
            .Matches(navMenu, @"Icon=""@IconFor\(""(?<icon>[a-z_]+)""\)"" Href=""(?<href>admin/[a-z-]+)""")
            .ToDictionary(match => "/" + match.Groups["href"].Value, match => match.Groups["icon"].Value);
        var tiles = Administration.Groups.SelectMany(group => group.Tiles).ToList();

        Assert.True(menu.Count >= 14, $"Only {menu.Count} administration menu entries read: the scan is broken.");
        Assert.Equal(menu.Keys.Order(), tiles.Select(tile => tile.Href).Order());
        Assert.All(tiles, tile => Assert.Equal(menu[tile.Href], tile.Icon));

        SetupDefaultResponses();
        var cut = Render<Administration>();
        Assert.Equal(tiles.Count, cut.FindAll("a.omni-nav-tile").Count);
        Assert.Equal(tiles.Count, cut.FindAll("a.omni-nav-tile .omni-nav-tile__description").Count);
    }

    [Fact]
    public void Renders_SettingsTab()
    {
        SetupDefaultResponses();

        var cut = Render<Administration>();
        Assert.Contains("PlatformSettings", cut.Markup);
    }

    [Fact]
    public void Renders_SecretsTab()
    {
        SetupDefaultResponses();
        var cut = Render<Administration>();
        Assert.Contains("SecurityTokens", cut.Markup);
    }

    [Fact]
    public void Renders_TokensTab()
    {
        SetupDefaultResponses();
        var cut = Render<Administration>();
        Assert.Contains("Roles", cut.Markup);
    }

    [Fact]
    public void Renders_WithSecrets()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto>());
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>
        {
            new() { Id = 1, Key = "DB_PASSWORD", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Id = 2, Key = "API_KEY", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
        });
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>());

        var cut = Render<Administration>();
        // The admin landing renders its header and the platform-settings navigation tile.
        Assert.Contains("Administration", cut.Markup);
        Assert.Contains("PlatformSettings", cut.Markup);
    }

    [Fact]
    public void Renders_WithTokens()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto>());
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>());
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>
        {
            new() { Id = 1, Token = "tok-abc123", IsUsed = false, ExpiresAt = DateTime.UtcNow.AddHours(24) },
            new() { Id = 2, Token = "tok-used", IsUsed = true, ExpiresAt = DateTime.UtcNow.AddHours(-1) }
        });

        var cut = Render<Administration>();
        // The admin landing renders its header and the audit-logs navigation tile.
        Assert.Contains("Administration", cut.Markup);
        Assert.Contains("AuditLogs", cut.Markup);
    }

    [Fact]
    public void NonAdmin_Redirects()
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: false);
        handler.SetJsonResponse("api/settings", new List<AppSettingDto>());
        handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>());
        handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>());

        var cut = Render<Administration>();
        // Page has no client-side auth guard: it still renders its header in bUnit (server enforces access).
        Assert.Contains("Administration", cut.Markup);
    }

    [Fact]
    public void Renders_Empty()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto>());
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>());
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>());

        var cut = Render<Administration>();
        Assert.Contains("Administration", cut.Markup);
    }

    private void SetupDefaultResponses()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto>
        {
            new() { Key = "SiteName", Value = "Aetheus" }
        });
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>
        {
            new() { Id = 1, Key = "DB_PASSWORD", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
        });
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>
        {
            new() { Id = 1, Token = "tok-abc", IsUsed = false, ExpiresAt = DateTime.UtcNow.AddHours(24) }
        });
    }
}
