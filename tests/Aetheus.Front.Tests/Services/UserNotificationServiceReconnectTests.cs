// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Services;

/// <summary>Verifies recovery after an initial notification-hub connection failure using a
/// deterministic failing hub factory.</summary>
public class UserNotificationServiceReconnectTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly BunitTestHelper.TestHandler _handler;

    public UserNotificationServiceReconnectTests() => _handler = BunitTestHelper.RegisterServices(this);

    private UserNotificationService CreateService() => new(
        Services.GetRequiredService<HubConnectionFactory>(),
        Services.GetRequiredService<AuthStateProvider>(),
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<PermissionService>(),
        TimeProvider.System)
    { DebounceMilliseconds = 1 };

    private static Task ReconcileAsync(UserNotificationService sut) =>
        (Task)typeof(UserNotificationService).GetMethod("ReconcileAsync", Priv)!.Invoke(sut, null)!;

    private static UserPermissionSummaryDto Summary(params EffectivePermissionDto[] permissions) =>
        new() { EffectivePermissions = [.. permissions] };

    private static EffectivePermissionDto Read(ResourceType type, int? id = null) =>
        new() { ResourceType = type, ResourceId = id, Permission = Permission.Read };

    [Fact]
    public async Task Reconcile_WhenThePageNeverLoadedItsPermissions_LoadsThem_WithoutPrompting()
    {
        // The page loaded while the backend was still starting: the permission set was never loaded.
        _handler.SetJsonResponse("api/users/me/permissions", Summary(Read(ResourceType.Project), Read(ResourceType.Server, 4)));
        var permissions = Services.GetRequiredService<PermissionService>();
        permissions.Clear();
        Assert.False(permissions.IsLoaded);
        var sut = CreateService();
        var prompted = false;
        sut.OnPermissionsChanged += () => prompted = true;

        await ReconcileAsync(sut);

        Assert.False(prompted);
        Assert.True(permissions.IsLoaded);
        Assert.True(permissions.CanRead(ResourceType.Server, 4));
        await sut.DisposeAsync();
    }

    [Fact]
    public async Task Reconcile_WhenTheLoadedPermissionsReallyDiffer_StillPrompts()
    {
        var permissions = Services.GetRequiredService<PermissionService>();
        permissions.SetPermissions([Read(ResourceType.Project)], isAdmin: false);
        _handler.SetJsonResponse("api/users/me/permissions", Summary(Read(ResourceType.Project), Read(ResourceType.Server, 4)));
        var sut = CreateService();
        var prompted = false;
        sut.OnPermissionsChanged += () => prompted = true;

        await ReconcileAsync(sut);

        Assert.True(prompted);
        await sut.DisposeAsync();
    }

    private static object? GetPrivate(UserNotificationService sut, string field) =>
        typeof(UserNotificationService).GetField(field, Priv)!.GetValue(sut);

    [Fact]
    public async Task StartAsync_InitialConnectFails_DoesNotThrow_AndArmsRetryLoop()
    {
        var sut = CreateService();

        await sut.StartAsync();

        // The hub is created and assigned (StartAsync never nulls it back out on failure), and a
        // retry loop is armed so the service is not permanently blind for the rest of the session.
        Assert.NotNull(GetPrivate(sut, "_hub"));
        Assert.NotNull(GetPrivate(sut, "_startRetryCts"));

        await sut.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_AfterFailedStart_CancelsRetryLoop_LeavesItNull()
    {
        var sut = CreateService();
        await sut.StartAsync();

        await sut.DisposeAsync();

        // Disposal must cleanly cancel the pending retry loop (no lingering CTS reference) rather
        // than leaving a zombie background task running against a disposed service.
        Assert.Null(GetPrivate(sut, "_startRetryCts"));
        Assert.Null(GetPrivate(sut, "_hub"));
    }

    [Fact]
    public async Task StartAsync_SecondCallWhileRetrying_StaysIdempotent()
    {
        var sut = CreateService();
        await sut.StartAsync();
        var hubBefore = GetPrivate(sut, "_hub");

        await sut.StartAsync();

        Assert.Same(hubBefore, GetPrivate(sut, "_hub"));

        await sut.DisposeAsync();
    }
}
