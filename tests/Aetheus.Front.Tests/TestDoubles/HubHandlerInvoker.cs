// SPDX-License-Identifier: EUPL-1.2
using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.SignalR.Client;

namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// Delivers a server-to-client message to the handlers a component registered with
/// <c>HubConnection.On(...)</c>, without a running hub. The connection keeps its handlers in a private
/// dictionary keyed by method name; this reads it by reflection so a test exercises the component's
/// real subscription instead of a private method standing in for it. Fails loudly when the SignalR
/// internals change shape, rather than silently invoking nothing.
/// </summary>
internal static class HubHandlerInvoker
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static async Task InvokeAsync(HubConnection hub, string methodName, params object?[] arguments)
    {
        var handlersField = typeof(HubConnection).GetField("_handlers", Instance)
            ?? throw new InvalidOperationException("HubConnection._handlers not found: SignalR internals changed.");
        var dictionary = (IDictionary)handlersField.GetValue(hub)!;
        var list = dictionary[methodName]
            ?? throw new InvalidOperationException($"No handler registered for '{methodName}'.");
        var handlers = (Array)list.GetType().GetMethod("GetHandlers", Instance)!.Invoke(list, null)!;
        Assert.NotEmpty(handlers.Cast<object>());
        foreach (var handler in handlers)
        {
            var invoke = handler!.GetType().GetMethod("InvokeAsync", Instance)
                ?? throw new InvalidOperationException("InvocationHandler.InvokeAsync not found: SignalR internals changed.");
            await (Task)invoke.Invoke(handler, [arguments])!;
        }
    }
}
