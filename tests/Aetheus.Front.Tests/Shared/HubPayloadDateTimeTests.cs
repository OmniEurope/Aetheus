// SPDX-License-Identifier: EUPL-1.2
using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// The server detail shows the same inventory timestamps whether the page read them over HTTP (JsonOptions.Web
/// localises UTC) or a live heartbeat pushed them over SignalR. The hub protocol used default options, so a
/// pushed "...Z" value stayed UTC: "Dernière MAJ de la base" of RKHunter showed the browser-local time after
/// a page load, then the UTC clock time once the next heartbeat arrived. Both paths must agree.
/// </summary>
public sealed class HubPayloadDateTimeTests : BunitContext
{
    private const string FileMtimeUtc = "2026-10-24T06:09:58Z";

    public HubPayloadDateTimeTests() => BunitTestHelper.RegisterServices(this);

    private HubConnection Connection() => new HubConnectionFactory(
        new ConfigurationBuilder().Build(),
        Services.GetRequiredService<AuthStateProvider>(),
        NullLogger<AuthDelegatingHandler>.Instance).Create("servers");

    private sealed class Binder(params Type[] parameters) : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => parameters;
        public Type GetReturnType(string invocationId) => typeof(object);
        public Type GetStreamItemType(string streamId) => typeof(object);
    }

    /// <summary>Delivers <paramref name="argumentJson"/> as the single argument of a hub invocation through
    /// the very protocol the connection built by the factory uses, and returns what a handler would receive.</summary>
    private static T ReceiveOverHub<T>(HubConnection connection, string argumentJson)
    {
        var field = typeof(HubConnection).GetField("_protocol", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HubConnection._protocol not found: SignalR internals changed.");
        var protocol = (IHubProtocol)field.GetValue(connection)!;
        var frame = $$"""{"type":1,"target":"Heartbeat","arguments":[{{argumentJson}}]}""" + '\u001e';
        var buffer = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(frame));
        Assert.True(protocol.TryParseMessage(ref buffer, new Binder(typeof(T)), out var message));
        return (T)((InvocationMessage)message!).Arguments![0]!;
    }

    private static string RkhunterJson =>
        $$"""{"isInstalled":true,"databaseLastUpdated":"{{FileMtimeUtc}}","lastScanTime":"{{FileMtimeUtc}}"}""";

    [Fact]
    public void PushedHeartbeatTimestamp_IsLocalised_LikeTheHttpOne()
    {
        var instant = DateTime.Parse(FileMtimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

        var pushed = ReceiveOverHub<RkhunterDataDto>(Connection(), RkhunterJson);

        Assert.Equal(instant.ToLocalTime(), pushed.DatabaseLastUpdated);
        Assert.Equal(DateTimeKind.Local, pushed.DatabaseLastUpdated.Kind);
        Assert.Equal(instant.ToLocalTime(), pushed.LastScanTime);
    }

    [Fact]
    public void HttpAndHubPaths_ShowTheSameTimestamp()
    {
        var overHttp = JsonSerializer.Deserialize<RkhunterDataDto>(RkhunterJson, JsonOptions.Web)!;
        var overHub = ReceiveOverHub<RkhunterDataDto>(Connection(), RkhunterJson);

        Assert.Equal(overHttp.DatabaseLastUpdated, overHub.DatabaseLastUpdated);
        Assert.Equal(overHttp.DatabaseLastUpdated.Kind, overHub.DatabaseLastUpdated.Kind);
        Assert.Equal(overHttp.DatabaseLastUpdated.ToString("g"), overHub.DatabaseLastUpdated.ToString("g"));
    }

    [Fact]
    public void HubSentinels_StayUnshifted()
    {
        var pushed = ReceiveOverHub<RkhunterDataDto>(
            Connection(), """{"isInstalled":true,"databaseLastUpdated":"0001-01-01T00:00:00Z"}""");

        Assert.Equal(default, pushed.DatabaseLastUpdated);
    }
}
