// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Hubs;

/// <summary>
/// A hub method refuses a call on purpose by throwing <see cref="HubException"/> (access denied, unknown
/// run...): the client receives the message and nothing else changes. SignalR's dispatcher logs every
/// such throw at Error, which buried real failures in the production log. This filter writes the refusal
/// at Information (hub, method, user, reason - never the arguments) and rethrows the same exception, so
/// the client contract is untouched; the file logger then drops the dispatcher's Error echo of that
/// exact exception type (<see cref="Aetheus.Back.Middleware.JsonFileLoggerProvider"/>). Any other
/// exception passes through untouched and stays an Error.
/// </summary>
public sealed class HubRefusalLogFilter(ILogger<HubRefusalLogFilter> logger) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext).ConfigureAwait(false);
        }
        catch (HubException refusal)
        {
            logger.LogInformation(
                "Hub call refused: {Hub}.{Method} by user {UserId}: {Reason}",
                invocationContext.Hub.GetType().Name,
                invocationContext.HubMethodName,
                invocationContext.Context.UserIdentifier,
                refusal.Message);
            throw;
        }
    }
}
