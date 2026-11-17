// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Tests NavMenu logic methods via reflection (no render - NavMenu needs AlertNotificationService
/// which is not part of the standard BunitTestHelper harness).
/// </summary>
public class NavMenuMethodTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type NavType = typeof(NavMenu);

    public NavMenuMethodTests() => BunitTestHelper.RegisterServices(this);

    // === IsOnSection ===

    [Theory]
    [InlineData("servers", "servers")]
    [InlineData("servers", "tasks")]
    [InlineData("servers", "logs")]
    [InlineData("servers", "alerts")]
    [InlineData("servers", "add-agent")]
    [InlineData("projects", "projects")]
    [InlineData("projects", "pipelines")]
    [InlineData("projects", "releases")]
    [InlineData("projects", "variable-libraries")]
    [InlineData("projects", "vaults")]
    [InlineData("projects", "environments")]
    [InlineData("projects", "git-repositories")]
    [InlineData("settings", "settings")]
    [InlineData("admin", "admin")]
    [InlineData("admin", "users")]
    [InlineData("admin", "audit")]
    [InlineData("admin", "plugins")]
    [InlineData("admin", "dashboards")]
    public void IsOnSection_KnownPaths_ReturnsTrue(string section, string path)
    {
        var instance = CreateInstance(path);
        var method = NavType.GetMethod("IsOnSection", Priv)!;
        var result = (bool)method.Invoke(instance, [section])!;
        Assert.True(result, $"IsOnSection(\"{section}\") should be true for path \"{path}\"");
    }

    [Theory]
    [InlineData("servers", "projects/1")]
    [InlineData("projects", "servers/5")]
    [InlineData("settings", "admin/users")]
    [InlineData("admin", "servers")]
    [InlineData("projects", "pipelines/schedule")]
    [InlineData("projects", "pipelines/schedule?view=week")]
    public void IsOnSection_WrongPaths_ReturnsFalse(string section, string path)
    {
        var instance = CreateInstance(path);
        var method = NavType.GetMethod("IsOnSection", Priv)!;
        var result = (bool)method.Invoke(instance, [section])!;
        Assert.False(result, $"IsOnSection(\"{section}\") should be false for path \"{path}\"");
    }

    [Fact]
    public void IsOnSection_UnknownSection_FallsBackToStartsWith()
    {
        var instance = CreateInstance("help/articles");
        var method = NavType.GetMethod("IsOnSection", Priv)!;
        var result = (bool)method.Invoke(instance, ["help"])!;
        Assert.True(result);
    }

    // === GetSpecificServerId ===

    [Theory]
    [InlineData("servers/42/overview", 42)]
    [InlineData("servers/1", 1)]
    [InlineData("servers/999/docker", 999)]
    public void GetSpecificServerId_MatchesId(string path, int expectedId)
    {
        var instance = CreateInstance(path);
        var method = NavType.GetMethod("GetSpecificServerId", Priv)!;
        var result = (int?)method.Invoke(instance, []);
        Assert.Equal(expectedId, result);
    }

    [Theory]
    [InlineData("servers")]
    [InlineData("projects/1")]
    [InlineData("settings")]
    public void GetSpecificServerId_NoMatch_ReturnsNull(string path)
    {
        var instance = CreateInstance(path);
        var method = NavType.GetMethod("GetSpecificServerId", Priv)!;
        var result = (int?)method.Invoke(instance, []);
        Assert.Null(result);
    }

    // === GetSpecificProjectId ===

    [Theory]
    [InlineData("projects/10/edit", 10)]
    [InlineData("projects/1", 1)]
    public void GetSpecificProjectId_DirectMatch_ReturnsId(string path, int expectedId)
    {
        var instance = CreateInstance(path);
        var method = NavType.GetMethod("GetSpecificProjectId", Priv)!;
        var result = (int?)method.Invoke(instance, []);
        Assert.Equal(expectedId, result);
    }

    [Fact]
    public void GetSpecificProjectId_GitRepoWithProjectId_ReturnsId()
    {
        var instance = CreateInstance("git-repositories?projectId=5");
        var method = NavType.GetMethod("GetSpecificProjectId", Priv)!;
        var result = (int?)method.Invoke(instance, []);
        Assert.Equal(5, result);
    }

    [Fact]
    public void GetSpecificProjectId_GitRepoWithExtraParams_ReturnsId()
    {
        var instance = CreateInstance("git-repositories/3?foo=bar&projectId=7");
        var method = NavType.GetMethod("GetSpecificProjectId", Priv)!;
        var result = (int?)method.Invoke(instance, []);
        Assert.Equal(7, result);
    }

    // === Helpers ===

    private NavMenu CreateInstance(string currentPath = "")
    {
        var instance = new NavMenu();
        NavType.GetField("_currentPath", Priv)!.SetValue(instance, currentPath);
        var projectNav = new ProjectNavContextService(Services.GetRequiredService<NavigationManager>());
        NavType.GetProperty("ProjectNav", Priv)!.SetValue(instance, projectNav);
        return instance;
    }
}
